using System.Diagnostics;
using System.Text;
using OpenReflect.Core;
using OpenReflect.Core.Abstractions;

namespace OpenReflect.Schedule;

/// <summary>Windows Task Scheduler integration via schtasks.exe (zero dependencies, scriptable,
/// fully testable). Tasks run under the current user.</summary>
public sealed class SchtasksScheduler : IScheduler
{
    public async Task<Result> RegisterAsync(JobScheduleSpec spec, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(spec.Name)) return Error.Of(ErrorCode.InvalidArgument, "schedule name is required");
        if (spec.Kind == ScheduleKind.Disabled)
            return Error.Of(ErrorCode.InvalidArgument, "schedule kind Disabled cannot be registered");
        var tr = $"/Create /F /TN \"{spec.Name}\" /TR \"{spec.CommandLine}\"";
        var args = spec.Kind switch
        {
            ScheduleKind.Daily => $"{tr} /SC DAILY /ST {spec.TimeOfDay}",
            ScheduleKind.Weekly => $"{tr} /SC WEEKLY /D {DaysArg(spec.DaysOfWeek)} /ST {spec.TimeOfDay}",
            ScheduleKind.Monthly => $"{tr} /SC MONTHLY /D 1 /ST {spec.TimeOfDay}",
            ScheduleKind.Logon => $"{tr} /SC ONLOGON",
            _ => throw new InvalidOperationException("unreachable schedule kind"),
        };
        var (code, output) = await RunSchtasksAsync(args, ct).ConfigureAwait(false);
        return code == 0 ? Result.Ok() : Error.Of(ErrorCode.SchedulerFailure, $"schtasks create failed (exit {code})", output.Trim());
    }

    public async Task<Result<IReadOnlyList<JobScheduleSpec>>> ListAsync(CancellationToken ct = default)
    {
        var (code, output) = await RunSchtasksAsync("/Query /FO CSV /NH", ct).ConfigureAwait(false);
        if (code != 0) return Error.Of(ErrorCode.SchedulerFailure, "schtasks query failed", output.Trim());
        var list = new List<JobScheduleSpec>();
        foreach (var line in output.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            var parts = line.Split("\",\"");
            if (parts.Length == 0) continue;
            var name = parts[0].Trim(new[] { '"', '\\' });
            if (name.StartsWith("OpenReflect-", StringComparison.OrdinalIgnoreCase))
                list.Add(new JobScheduleSpec(name, parts.Length > 1 ? parts[1].TrimEnd('"') : "", "", ScheduleKind.Daily, ""));
        }
        return Result<IReadOnlyList<JobScheduleSpec>>.Ok(list);
    }

    public async Task<Result> DeleteAsync(string name, CancellationToken ct = default)
    {
        var (code, output) = await RunSchtasksAsync($"/Delete /F /TN \"{name}\"", ct).ConfigureAwait(false);
        if (code != 0)
        {
            if (output.Contains("does not exist", StringComparison.OrdinalIgnoreCase) || output.Contains("ERROR: The system cannot find", StringComparison.OrdinalIgnoreCase))
                return Error.Of(ErrorCode.JobNotFound, $"scheduled task not found: {name}");
            return Error.Of(ErrorCode.SchedulerFailure, $"schtasks delete failed (exit {code})", output.Trim());
        }
        return Result.Ok();
    }

    public async Task<Result> RunOnceAsync(string name, CancellationToken ct = default)
    {
        var (code, output) = await RunSchtasksAsync($"/Run /TN \"{name}\"", ct).ConfigureAwait(false);
        return code == 0 ? Result.Ok() : Error.Of(ErrorCode.SchedulerFailure, $"schtasks run failed (exit {code})", output.Trim());
    }

    private static string DaysArg(IReadOnlyList<string>? days) =>
        days is null || days.Count == 0 ? "MON" : string.Join(",", days);

    private static async Task<(int Code, string Output)> RunSchtasksAsync(string args, CancellationToken ct)
    {
        var psi = new ProcessStartInfo("schtasks.exe", args)
        {
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            CreateNoWindow = true,
            StandardOutputEncoding = Encoding.UTF8,
        };
        using var p = Process.Start(psi)!;
        var output = await p.StandardOutput.ReadToEndAsync(ct).ConfigureAwait(false);
        output += await p.StandardError.ReadToEndAsync(ct).ConfigureAwait(false);
        await p.WaitForExitAsync(ct).ConfigureAwait(false);
        return (p.ExitCode, output);
    }
}

/// <summary>Headless job execution (S33): loads a job JSON and runs the backup via ImageEngine.
/// v0.1 chain policy: full → new root; incremental/differential → against the newest matching image.</summary>
public sealed class JobRunner
{
    public async Task<Result<ImageHeaderInfo>> RunAsync(string jobJsonPath, bool dryRun = false, IProgress<ProgressInfo>? progress = null, CancellationToken ct = default)
    {
        var loaded = JobConfig.Load(jobJsonPath);
        if (!loaded.IsSuccess) return Result<ImageHeaderInfo>.Fail(loaded.Error);
        var job = loaded.Value;

        var optsResult = job.ToImageOptions();
        if (!optsResult.IsSuccess) return Result<ImageHeaderInfo>.Fail(optsResult.Error);
        var options = optsResult.Value;

        if (dryRun)
        {
            // validate everything except writing: source must open
            var probe = await new PassThroughSnapshotProvider().AcquireAsync(job.Source.Path, true, ct).ConfigureAwait(false);
            if (!probe.IsSuccess) return Result<ImageHeaderInfo>.Fail(probe.Error);
            await probe.Value.DisposeAsync().ConfigureAwait(false);
            return new ImageHeaderInfo(Guid.Empty, Guid.Empty, ChainKind.Full, options.ChunkSizeBytes, false, true, 0, 0, "dry-run", DateTimeOffset.UtcNow);
        }

        Directory.CreateDirectory(job.Destination.Directory);
        var stamp = DateTime.Now.ToString("yyyyMMdd-HHmmss");
        var dest = Path.Combine(job.Destination.Directory, $"{job.Destination.FileNamePrefix}-{stamp}-{job.Chain.Kind}.orf");
        var log = new FileLogSink(job.Destination.Directory, $"job-{job.JobId:N}");
        options = options with { Log = log, SourceLabel = job.Source.Path };

        var engine = new ImageEngine();
        var kind = job.Chain.Kind switch
        {
            "full" => ChainKind.Full,
            "incremental" => ChainKind.Incremental,
            _ => ChainKind.Differential,
        };
        return kind switch
        {
            ChainKind.Full => await engine.CreateFullAsync(job.Source.Path, dest, options, progress, ct).ConfigureAwait(false),
            _ => await RunAgainstNewestAsync(engine, job, kind, dest, options, progress, ct).ConfigureAwait(false),
        };
    }

    private static async Task<Result<ImageHeaderInfo>> RunAgainstNewestAsync(ImageEngine engine, JobDocument job, ChainKind kind, string dest, ImageOptions options, IProgress<ProgressInfo>? progress, CancellationToken ct)
    {
        var candidates = Directory.Exists(job.Destination.Directory)
            ? Directory.GetFiles(job.Destination.Directory, $"{job.Destination.FileNamePrefix}-*.orf")
            : Array.Empty<string>();
        if (candidates.Length == 0)
            return Error.Of(ErrorCode.ChainBroken, $"no existing image to base the {kind} on; run a full backup first");
        var newest = candidates.OrderByDescending(File.GetLastWriteTimeUtc).First();
        return kind == ChainKind.Incremental
            ? await engine.CreateIncrementalAsync(job.Source.Path, newest, dest, options, progress, ct).ConfigureAwait(false)
            : await engine.CreateDifferentialAsync(job.Source.Path, newest, dest, options, progress, ct).ConfigureAwait(false);
    }
}
