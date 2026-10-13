using System.Reflection;
using OpenReflect.Core;
using OpenReflect.Core.Abstractions;
using OpenReflect.Restore;
using OpenReflect.Schedule;
using OpenReflect.Verify;

namespace OpenReflect.CLI;

/// <summary>OpenReflect command line. Exit codes: 0 ok, 2 error result, 3 unhandled exception.</summary>
public static class Program
{
    public static string Version =>
        Assembly.GetExecutingAssembly().GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion
        ?? Assembly.GetExecutingAssembly().GetName().Version?.ToString() ?? "0.1.0";

    public static async Task<int> Main(string[] args)
    {
        try
        {
            if (args.Length == 0 || args[0] is "-h" or "--help" or "help") { PrintHelp(); return 0; }
            if (args[0] is "--version" or "version") { Console.WriteLine($"OpenReflect {Version}"); return 0; }
            return args[0] switch
            {
                "create" => await CmdCreate(args[1..]),
                "verify" => await CmdVerify(args[1..]),
                "restore" => await CmdRestore(args[1..]),
                "schedule" => await CmdSchedule(args[1..]),
                "jobs" => await CmdJobs(args[1..]),
                var other => Unknown(other),
            };
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"unhandled: {ex}");
            return 3;
        }
    }

    private static int Unknown(string cmd)
    {
        Console.Error.WriteLine($"unknown command '{cmd}' — try `OpenReflect help`");
        return 2;
    }

    private static void PrintHelp()
    {
        Console.WriteLine($"""
        OpenReflect {Version} — open-source disk imaging (full / incremental / differential)

        usage: OpenReflect <command> [options]

        commands:
          create    --source <file|device> --dest <dir> [--prefix p] [--kind full|incremental|differential]
                      [--parent <image.orf>] [--chunk-size <bytes>] [--compress none|deflate] [--level 1-9]
                      [--encrypt] [--iterations n] [--password-env VAR] [--no-skip-zero] [--log-dir <dir>]
          verify    <image.orf> [--depth structure|manifest|full]
          restore   <image.orf> --target <file> [--dry-run] [--confirm]
                      (v0.1 restores to image files / mounted VHDs only — no physical devices)
          schedule  add <name> <job.json> <daily|weekly|monthly|logon> [HH:mm] | list | remove <name> | run <name>
          jobs      <job.json> [--dry-run]

        notes:
          encrypted images take the password from the environment variable given at create time (--password-env).
        """);
    }

    private static Dictionary<string, string> ParseArgs(string[] args)
    {
        var map = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        for (int i = 0; i < args.Length; i++)
        {
            var a = args[i];
            if (a.StartsWith("--"))
            {
                if (i + 1 < args.Length && !args[i + 1].StartsWith("--")) { map[a[2..]] = args[++i]; }
                else map[a[2..]] = "true";
            }
        }
        return map;
    }

    private static async Task<int> CmdCreate(string[] args)
    {
        var a = ParseArgs(args);
        if (!a.TryGetValue("source", out var source) || !a.TryGetValue("dest", out var destDir))
        {
            Console.Error.WriteLine("create requires --source and --dest");
            return 2;
        }
        string password() => a.TryGetValue("password-env", out var envVar) ? Environment.GetEnvironmentVariable(envVar) ?? "" : "";
        var options = new ImageOptions
        {
            ChunkSizeBytes = a.TryGetValue("chunk-size", out var cs) ? long.Parse(cs) : 4 * 1024 * 1024,
            Compression = a.TryGetValue("compress", out var comp) && comp == "none" ? CompressionKind.None : CompressionKind.Deflate,
            CompressionLevel = a.TryGetValue("level", out var lvl) ? int.Parse(lvl) : 6,
            Encryption = a.ContainsKey("encrypt") ? EncryptionKind.Aes256Gcm : EncryptionKind.None,
            KdfIterations = a.TryGetValue("iterations", out var it) ? int.Parse(it) : 600000,
            PasswordProvider = password,
            SkipZeroRegions = !a.ContainsKey("no-skip-zero"),
            Log = a.TryGetValue("log-dir", out var logDir) ? new FileLogSink(logDir) : null,
        };
        var prefix = a.TryGetValue("prefix", out var p) ? p : "image";
        var kind = a.TryGetValue("kind", out var k) ? k : "full";
        Directory.CreateDirectory(destDir);
        var dest = Path.Combine(destDir, $"{prefix}-{DateTime.Now:yyyyMMdd-HHmmss}-{kind}.orf");

        var engine = new ImageEngine();
        Result<ImageHeaderInfo> result;
        var sw = System.Diagnostics.Stopwatch.StartNew();
        if (kind == "full") result = await engine.CreateFullAsync(source, dest, options);
        else if (kind == "incremental")
        {
            if (!a.TryGetValue("parent", out var parent)) { Console.Error.WriteLine("incremental requires --parent <image.orf>"); return 2; }
            result = await engine.CreateIncrementalAsync(source, parent, dest, options);
        }
        else
        {
            if (!a.TryGetValue("parent", out var root)) { Console.Error.WriteLine("differential requires --parent <root.orf>"); return 2; }
            result = await engine.CreateDifferentialAsync(source, root, dest, options);
        }

        if (!result.IsSuccess)
        {
            Console.Error.WriteLine($"create failed: {result.Error}");
            return 2;
        }
        var size = new FileInfo(dest).Length;
        Console.WriteLine($"created {dest}");
        Console.WriteLine($"  chunks={result.Value.ChunkCount} logical={result.Value.LogicalSizeBytes:N0} B  file={size:N0} B  {sw.Elapsed.TotalSeconds:F1}s");
        return 0;
    }

    private static async Task<int> CmdVerify(string[] args)
    {
        var a = ParseArgs(args);
        if (args.Length == 0) { Console.Error.WriteLine("verify requires <image.orf>"); return 2; }
        var depth = a.TryGetValue("depth", out var d) ? d switch
        {
            "structure" => VerifyDepth.Structure,
            "manifest" => VerifyDepth.Manifest,
            _ => VerifyDepth.Full,
        } : VerifyDepth.Full;
        string password() => a.TryGetValue("password-env", out var env) ? Environment.GetEnvironmentVariable(env) ?? "" : "";
        var verifier = new Verifier();
        var result = await verifier.VerifyAsync(args[0], depth);
        if (result.Success)
        {
            Console.WriteLine($"OK: {result.ImagePath} (complete={result.IsComplete}, chunks checked={result.ChunksChecked}, depth={depth})");
            return 0;
        }
        Console.Error.WriteLine(result.Error is not null ? $"verify failed: {result.Error}" : "verify FAILED:");
        foreach (var f in result.FailedChunks) Console.Error.WriteLine($"  chunk {f.ChunkIndex}: {f.Reason}");
        return 2;
    }

    private static async Task<int> CmdRestore(string[] args)
    {
        var a = ParseArgs(args);
        if (args.Length == 0 || !a.TryGetValue("target", out var target))
        {
            Console.Error.WriteLine("restore requires <image.orf> and --target <file>");
            return 2;
        }
        string password() => a.TryGetValue("password-env", out var env) ? Environment.GetEnvironmentVariable(env) ?? "" : "";
        var restorer = new Restorer();
        var plan = await restorer.PlanAsync(args[0], target, passwordProvider: password);
        if (!plan.IsSuccess) { Console.Error.WriteLine($"plan failed: {plan.Error}"); return 2; }
        Console.WriteLine($"plan: {plan.Value.Regions.Count} regions, {plan.Value.BytesToWrite:N0} B → {plan.Value.TargetPath}");
        foreach (var img in plan.Value.ChainImages) Console.WriteLine($"  chain: {img}");

        bool confirmed = a.ContainsKey("confirm") || a.ContainsKey("i-understand-this-wipes-the-target");
        var exec = await restorer.ExecuteAsync(plan.Value, new RestoreOptions
        {
            DryRun = a.ContainsKey("dry-run"),
            Confirmed = confirmed,
            PasswordProvider = password,
        });
        if (!exec.IsSuccess) { Console.Error.WriteLine($"restore failed: {exec.Error}"); return 2; }
        Console.WriteLine(a.ContainsKey("dry-run") ? "dry-run OK (nothing written)" : "restore complete");
        return 0;
    }

    private static async Task<int> CmdSchedule(string[] args)
    {
        var sched = new SchtasksScheduler();
        if (args.Length >= 1 && args[0] == "add" && args.Length >= 4)
        {
            var name = args[1].StartsWith("OpenReflect-") ? args[1] : $"OpenReflect-{args[1]}";
            var jobJson = Path.GetFullPath(args[2]);
            var exe = Path.Combine(AppContext.BaseDirectory, "OpenReflect.CLI.exe");
            var cmdline = $"\"{exe}\" jobs \"{jobJson}\"";
            var kind = args[3] switch { "weekly" => ScheduleKind.Weekly, "monthly" => ScheduleKind.Monthly, "logon" => ScheduleKind.Logon, _ => ScheduleKind.Daily };
            var time = args.Length >= 5 ? args[4] : "03:00";
            var r = await sched.RegisterAsync(new JobScheduleSpec(name, cmdline, AppContext.BaseDirectory, kind, time));
            if (!r.IsSuccess) { Console.Error.WriteLine($"schedule add failed: {r.Error}"); return 2; }
            Console.WriteLine($"scheduled '{name}' ({kind} {time}) → {cmdline}");
            return 0;
        }
        if (args.Length >= 1 && args[0] == "list")
        {
            var r = await sched.ListAsync();
            if (!r.IsSuccess) { Console.Error.WriteLine($"schedule list failed: {r.Error}"); return 2; }
            foreach (var s in r.Value) Console.WriteLine($"{s.Name}  [{s.Kind} {s.TimeOfDay}]  {s.CommandLine}");
            Console.WriteLine($"{r.Value.Count} OpenReflect task(s)");
            return 0;
        }
        if (args.Length >= 2 && args[0] == "remove")
        {
            var name = args[1].StartsWith("OpenReflect-") ? args[1] : $"OpenReflect-{args[1]}";
            var r = await sched.DeleteAsync(name);
            if (!r.IsSuccess) { Console.Error.WriteLine($"schedule remove failed: {r.Error}"); return 2; }
            Console.WriteLine($"removed {name}");
            return 0;
        }
        if (args.Length >= 2 && args[0] == "run")
        {
            var name = args[1].StartsWith("OpenReflect-") ? args[1] : $"OpenReflect-{args[1]}";
            var r = await sched.RunOnceAsync(name);
            if (!r.IsSuccess) { Console.Error.WriteLine($"schedule run failed: {r.Error}"); return 2; }
            Console.WriteLine($"triggered {name}");
            return 0;
        }
        Console.Error.WriteLine("schedule: add <name> <job.json> <daily|weekly|monthly|logon> [HH:mm] | list | remove <name> | run <name>");
        return 2;
    }

    private static async Task<int> CmdJobs(string[] args)
    {
        if (args.Length == 0) { Console.Error.WriteLine("jobs requires <job.json>"); return 2; }
        bool dryRun = args.Contains("--dry-run");
        var runner = new JobRunner();
        var result = await runner.RunAsync(args[0], dryRun);
        if (!result.IsSuccess) { Console.Error.WriteLine($"job failed: {result.Error}"); return 2; }
        Console.WriteLine($"job OK (dry-run={dryRun}): {result.Value.ImageId} chunks={result.Value.ChunkCount}");
        return 0;
    }
}
