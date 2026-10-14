using System.Reflection;
using System.Text.Json;
using OpenReflect.Core;
using OpenReflect.Core.Abstractions;
using OpenReflect.DiskAccess;
using OpenReflect.Format;
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
                "restore-disk" => await CmdRestoreDisk(args[1..]),
                "backup-disk" => await CmdBackupDisk(args[1..]),
                "disks" => await CmdDisks(),
                "inspect" => await CmdInspect(args[1..]),
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
          create       --source <file|device> --dest <dir> [--prefix p] [--kind full|incremental|differential]
                         [--parent <image.orf>] [--chunk-size <bytes>] [--compress none|deflate|zstd] [--level 1-9]
                         [--encrypt] [--iterations n] [--password-env VAR] [--no-skip-zero] [--log-dir <dir>]
          backup-disk  --disk <n> --dest <dir> [--name p] [--compress zstd|deflate|none] [--level 1-9]
                         [--no-intelligent] [--no-vss] [--password-env VAR] [--log-dir <dir>]
                         intelligent sector imaging of whole disk N with VSS consistency (0.2)
          verify       <image.orf> [--depth structure|manifest|full]
          restore      <image.orf> --target <file> [--dry-run] [--confirm]   (files / mounted VHDs only)
          restore-disk --image <orf> --disk <n> [--plan] [--confirm-device-write] [--skip-unwritten]
                         [--offline] [--no-verify] [--password-env VAR] [--task-file <json>]
                         guarded whole-disk restore: size match + running-OS refusal + exclusive open (0.2)
          disks        list disks, partitions and volumes
          inspect      <image.orf> print header facts
          schedule     add <name> <job.json> <daily|weekly|monthly|logon> [HH:mm] | list | remove <name> | run <name>
          jobs         <job.json> [--dry-run]

        notes:
          machine-readable progress is emitted as '##PROGRESS <json>' lines; final summary as '##DONE <json>'.
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

    /// <summary>Machine-readable progress line for script parsers (orf-backup.ps1 / offline-restore.cmd).</summary>
    private static void EmitProgress(ProgressInfo p)
    {
        var json = JsonSerializer.Serialize(new
        {
            phase = p.Phase,
            bytesDone = p.ProcessedBytes,
            bytesTotal = p.TotalBytes,
            chunksDone = p.ChunksDone,
            chunksTotal = p.ChunksTotal,
            speedBytesPerSec = (long)p.SpeedBytesPerSec,
        });
        Console.WriteLine($"##PROGRESS {json}");
    }

    private static string TypeLabel(DiskPartition p) => p.GptType switch
    {
        var t when t == DiskPartition.GptEfi => "EFI",
        var t when t == DiskPartition.GptMsr => "MSR",
        var t when t == DiskPartition.GptBasicData => "Basic",
        var t when t == DiskPartition.GptWindowsRecovery => "Recovery",
        _ => p.MbrType != 0 ? $"MBR 0x{p.MbrType:X2}" : (p.GptType == Guid.Empty ? "?" : p.GptType.ToString()),
    };

    private static async Task<int> CmdDisks()
    {
        var volumes = VolumeEnumerator.All();
        var byPartition = new Dictionary<(uint Disk, uint Part), VolumeRef>();
        if (volumes.IsSuccess)
            foreach (var v in volumes.Value)
                if (v.PartitionNumber > 0)
                    byPartition.TryAdd((v.DiskNumber, v.PartitionNumber), v);

        for (int n = 0; n < 32; n++)
        {
            var disk = await DiskLayout.ReadAsync(n).ConfigureAwait(false);
            if (!disk.IsSuccess) continue;
            var d = disk.Value;
            Console.WriteLine($"disk {n}: {d.SizeBytes / (1024.0 * 1024 * 1024):F1} GiB  {(d.IsGpt ? "GPT" : "MBR")}  sector={d.SectorSize}");
            foreach (var p in d.Partitions)
            {
                var vol = byPartition.TryGetValue(((uint)n, (uint)p.Number), out var v) ? v : null;
                var letter = vol?.DriveLetter is char c ? $"{c}:" : "--";
                var fs = vol?.FileSystem is { Length: > 0 } f ? f : "--";
                Console.WriteLine($"  part {p.Number,2}: {TypeLabel(p),-9} {letter,-2} {fs,-5} {p.Offset / (1024.0 * 1024),10:F0} MiB  {p.Length / (1024.0 * 1024 * 1024),9:F2} GiB  {p.Name}");
            }
        }
        return 0;
    }

    private static async Task<int> CmdInspect(string[] args)
    {
        if (args.Length == 0) { Console.Error.WriteLine("inspect requires <image.orf>"); return 2; }
        var opened = OrfImage.OpenAsync(args[0]).GetAwaiter().GetResult();
        if (!opened.IsSuccess) { Console.Error.WriteLine($"inspect failed: {opened.Error}"); return 2; }
        await using var r = opened.Value;
        var h = r.Header;
        var json = JsonSerializer.Serialize(new
        {
            imageId = h.ImageId, parentImageId = h.ParentImageId, chainKind = h.ChainKind.ToString(),
            chunkSizeBytes = h.ChunkSizeBytes, encrypted = h.Encrypted, complete = h.IsComplete,
            chunkCount = h.ChunkCount, logicalSizeBytes = h.LogicalSizeBytes,
            sourceLabel = h.SourceLabel, createdAt = h.CreatedAt.ToString("o"), filePath = r.FilePath,
        });
        Console.WriteLine(json);
        return 0;
    }

    private static async Task<int> CmdBackupDisk(string[] args)
    {
        var a = ParseArgs(args);
        if (!a.TryGetValue("disk", out var diskStr) || !int.TryParse(diskStr, out var diskNumber) || diskNumber < 0)
        { Console.Error.WriteLine("backup-disk requires --disk <n>"); return 2; }
        if (!a.TryGetValue("dest", out var destDir)) { Console.Error.WriteLine("backup-disk requires --dest <dir>"); return 2; }
        string password() => a.TryGetValue("password-env", out var envVar) ? Environment.GetEnvironmentVariable(envVar) ?? "" : "";

        var compression = a.TryGetValue("compress", out var comp) ? comp.ToLowerInvariant() : "zstd";
        var options = new ImageOptions
        {
            Compression = compression switch
            {
                "none" => CompressionKind.None,
                "deflate" => CompressionKind.Deflate,
                "zstd" => CompressionKind.Zstd,
                _ => CompressionKind.Zstd,
            },
            CompressionLevel = a.TryGetValue("level", out var lvl) ? int.Parse(lvl) : 3,
            Encryption = a.ContainsKey("encrypt") ? EncryptionKind.Aes256Gcm : EncryptionKind.None,
            KdfIterations = a.TryGetValue("iterations", out var it) ? int.Parse(it) : 600000,
            PasswordProvider = password,
            SourceLabel = a.TryGetValue("name", out var nm) ? nm : $"disk{diskNumber}",
            Log = a.TryGetValue("log-dir", out var logDir) ? new FileLogSink(logDir) : null,
        };

        var provider = new DiskSnapshotProvider(
            preferVss: !a.ContainsKey("no-vss"),
            intelligent: !a.ContainsKey("no-intelligent"),
            log: options.Log);
        var engine = new ImageEngine(provider);

        Directory.CreateDirectory(destDir);
        var name = a.TryGetValue("name", out var n) ? n : $"disk{diskNumber}";
        var dest = Path.Combine(destDir, $"{name}-{DateTime.Now:yyyyMMdd-HHmmss}.orf");
        var sw = System.Diagnostics.Stopwatch.StartNew();
        Console.WriteLine($"##START {{\"disk\":{diskNumber},\"dest\":\"{dest.Replace("\\", "\\\\")}\"}}");
        var result = await engine.CreateFullAsync(DiskLayout.DevicePrefix + diskNumber, dest, options,
            new Progress<ProgressInfo>(EmitProgress)).ConfigureAwait(false);
        if (!result.IsSuccess)
        {
            Console.Error.WriteLine($"backup-disk failed: {result.Error}");
            try { if (File.Exists(dest)) File.Delete(dest); } catch (IOException) { }
            return 2;
        }
        var json = JsonSerializer.Serialize(new
        {
            image = dest,
            logicalSizeBytes = result.Value.LogicalSizeBytes,
            chunkCount = result.Value.ChunkCount,
            fileBytes = new FileInfo(dest).Length,
            elapsedSeconds = Math.Round(sw.Elapsed.TotalSeconds, 1),
        });
        Console.WriteLine($"##DONE {json}");
        return 0;
    }

    private static async Task<int> CmdRestoreDisk(string[] args)
    {
        var a = ParseArgs(args);
        string password() => a.TryGetValue("password-env", out var envVar) ? Environment.GetEnvironmentVariable(envVar) ?? "" : "";

        string image;
        int diskNumber;
        bool skipUnwritten = a.ContainsKey("skip-unwritten");
        bool offline = a.ContainsKey("offline");
        bool verifyAfter = !a.ContainsKey("no-verify");
        if (a.TryGetValue("task-file", out var taskFile))
        {
            var task = JsonSerializer.Deserialize<Dictionary<string, JsonElement>>(File.ReadAllText(taskFile));
            if (task is null) { Console.Error.WriteLine("restore-disk: unreadable task file"); return 2; }
            image = task["image"].GetString()!;
            diskNumber = task["disk"].GetInt32();
            if (task.TryGetValue("skipUnwritten", out var su)) skipUnwritten = su.GetBoolean();
            if (task.TryGetValue("verifyAfter", out var va)) verifyAfter = va.GetBoolean();
            if (task.TryGetValue("offline", out var of)) offline = of.GetBoolean();
        }
        else
        {
            if (args.Length == 0 || !a.TryGetValue("image", out image!) || !a.TryGetValue("disk", out var d) || !int.TryParse(d, out diskNumber))
            { Console.Error.WriteLine("restore-disk requires --image <orf> --disk <n> (or --task-file)"); return 2; }
        }

        var restorer = new DiskRestorer();
        var plan = await restorer.PlanAsync(image, diskNumber, passwordProvider: password).ConfigureAwait(false);
        if (!plan.IsSuccess) { Console.Error.WriteLine($"plan failed: {plan.Error}"); return 2; }

        var preflight = await restorer.PreflightAsync(plan.Value, diskNumber, offline).ConfigureAwait(false);
        if (!preflight.IsSuccess) { Console.Error.WriteLine($"preflight failed: {preflight.Error}"); return 2; }
        var pf = preflight.Value;
        Console.WriteLine($"##PLAN {{\"disk\":{diskNumber},\"diskBytes\":{pf.Disk.SizeBytes},\"imageBytes\":{plan.Value.TargetSizeBytes},\"regions\":{plan.Value.Regions.Count},\"hostsRunningOs\":{(pf.HostsRunningOs ? "true" : "false")},\"offline\":{(offline ? "true" : "false")}}}");
        if (a.ContainsKey("plan")) return pf.HostsRunningOs && !offline ? 2 : 0;

        var exec = await restorer.ExecuteToDiskAsync(plan.Value, diskNumber, new DiskRestoreOptions
        {
            Confirmed = a.ContainsKey("confirm-device-write"),
            OfflineEnvironment = offline,
            SkipUnwrittenRegions = skipUnwritten,
            VerifyAfterWrite = verifyAfter,
            PasswordProvider = password,
        }, new Progress<ProgressInfo>(EmitProgress)).ConfigureAwait(false);
        if (!exec.IsSuccess) { Console.Error.WriteLine($"restore-disk failed: {exec.Error}"); return 2; }
        Console.WriteLine($"##DONE {{\"disk\":{diskNumber},\"image\":\"{image.Replace("\\", "\\\\")}\"}}");
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
