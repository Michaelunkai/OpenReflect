using System.Threading.Channels;
using OpenReflect.Core.Abstractions;
using OpenReflect.DiskAccess;
using OpenReflect.Format;

namespace OpenReflect.Restore;

/// <summary>Options for a whole-disk restore. Every field is explicit: writing a device destroys it.</summary>
public sealed record DiskRestoreOptions
{
    public bool DryRun { get; init; } = false;
    public bool Confirmed { get; init; } = false;
    /// <summary>Set by the offline (WinRE) runner: the running OS root is X:\ there, so a volume on
    /// the target disk that happens to host Windows is not the running OS and may be overwritten.</summary>
    public bool OfflineEnvironment { get; init; } = false;
    /// <summary>Fast mode: never write regions the image stores as sparse-zero (unallocated space).
    /// Restored free-space clusters keep the target's previous content — filesystem-irrelevant,
    /// Macrium intelligent-restore parity. False = write explicit zeros (slower, fully wiped).</summary>
    public bool SkipUnwrittenRegions { get; init; } = false;
    /// <summary>Re-read every written region from the device and match stored SHA-256. Default on:
    /// the stakes of a bad device write justify the second pass.</summary>
    public bool VerifyAfterWrite { get; init; } = true;
    public Func<string?>? PasswordProvider { get; init; }
    public ILogSink? Log { get; init; }
}

/// <summary>The guarded device writer. Companion to <see cref="Restorer"/> (which keeps refusing
/// device targets); every gate runs BEFORE the first byte is written, in refusal-cheapest order:
/// device path → preflight (size, OS-on-target) → exclusive open (dismount retry) → Confirmed.
/// Writes decompress in parallel and land through one ordered writer. INCIDENT RULE: tests exercise
/// this against VHDs only, never real disks.</summary>
public sealed class DiskRestorer
{
    public async Task<Result<RestorePlan>> PlanAsync(string imageFile, int diskNumber, Func<string?>? passwordProvider = null, CancellationToken ct = default)
    {
        var restorer = new Restorer();
        return await restorer.PlanAsync(imageFile, DiskLayout.DevicePrefix + diskNumber, passwordProvider, ct).ConfigureAwait(false);
    }

    /// <summary>Read-only preflight of every gate checkable without opening the device exclusively.</summary>
    public async Task<Result<DiskPreflight>> PreflightAsync(RestorePlan plan, int diskNumber, bool offlineEnvironment, CancellationToken ct = default)
    {
        if (!plan.TargetPath.StartsWith(DiskLayout.DevicePrefix, StringComparison.OrdinalIgnoreCase))
            return Error.Of(ErrorCode.RestoreSafetyGateBlocked, $"disk restore target must be {DiskLayout.DevicePrefix}N, got {plan.TargetPath}");

        var layout = await DiskLayout.ReadAsync(diskNumber, ct).ConfigureAwait(false);
        if (!layout.IsSuccess) return Result<DiskPreflight>.Fail(layout.Error);
        var disk = layout.Value;

        if (disk.SizeBytes != plan.TargetSizeBytes)
            return Error.Of(ErrorCode.RestoreTargetTooSmall,
                $"disk {diskNumber} is {disk.SizeBytes} bytes but the image covers {plan.TargetSizeBytes} — a different-size target disk is refused (no resize in v0.2)");

        bool osOnDisk = false;
        VolumeRef? osVolume = null;
        var volumes = VolumeEnumerator.ForDisk((uint)diskNumber);
        var runningOs = VolumeEnumerator.RunningOsVolumeGuid();
        if (volumes.IsSuccess && runningOs is not null)
            foreach (var v in volumes.Value)
                if (string.Equals(v.VolumeGuidPath, runningOs, StringComparison.OrdinalIgnoreCase))
                { osOnDisk = true; osVolume = v; }

        return new DiskPreflight(disk, osOnDisk, osVolume?.VolumeGuidPath, volumes.IsSuccess ? volumes.Value.Count : 0);
    }

    public sealed record DiskPreflight(DiskInfo Disk, bool HostsRunningOs, string? RunningOsVolume, int VolumeCount);

    public async Task<Result> ExecuteToDiskAsync(RestorePlan plan, int diskNumber, DiskRestoreOptions o, IProgress<ProgressInfo>? progress = null, CancellationToken ct = default)
    {
        // ---- gates: path shape, size match, OS-on-target (nothing opened yet) ----
        var pre = await PreflightAsync(plan, diskNumber, o.OfflineEnvironment, ct).ConfigureAwait(false);
        if (!pre.IsSuccess) return Result.Fail(pre.Error);
        if (pre.Value.HostsRunningOs && !o.OfflineEnvironment)
            return Error.Of(ErrorCode.RestoreSafetyGateBlocked,
                $"disk {diskNumber} hosts the RUNNING operating system ({pre.Value.RunningOsVolume}). Overwriting it from inside Windows destroys the machine. Boot the offline restore environment (orf-restore.ps1); OfflineEnvironment is only valid from WinRE.");
        if (o.DryRun) return Result.Ok();
        if (!o.Confirmed)
            return Error.Of(ErrorCode.RestoreSafetyGateBlocked, "disk restore refused: Confirmed flag not set (D12)");

        var log = o.Log ?? NullLogSink.Instance;

        // ---- exclusive open, dismount retry ----
        var devicePath = DiskLayout.DevicePrefix + diskNumber;
        var opened = Win32.Open(devicePath, write: true, exclusive: true);
        if (!opened.IsSuccess && opened.Error.Code == ErrorCode.DestinationInUse)
        {
            log.Write(LogLevel.Warn, "restore", $"device {devicePath} busy; dismounting its volumes and retrying once");
            await DismountVolumesOfDiskAsync(diskNumber, log).ConfigureAwait(false);
            opened = Win32.Open(devicePath, write: true, exclusive: true);
        }
        if (!opened.IsSuccess) return Result.Fail(opened.Error);
        using var device = opened.Value;
        // a mounted filesystem on the target makes raw writes fail with ACCESS_DENIED and lets the
        // FS write cached pages back over our data — dismounting is MANDATORY, never best-effort
        if (!await DismountVolumesOfDiskAsync(diskNumber, log).ConfigureAwait(false))
            return Result.Fail(Error.Of(ErrorCode.DestinationInUse,
                $"disk {diskNumber} has volumes that could not be dismounted (open handles); refusing to write into a live volume"));
        using var target = new FileStream(device, FileAccess.Write, 1 << 16, isAsync: true);

        var readers = new List<IImageReader>();
        long written = 0, skippedSparse = 0, denyRetries = 0;
        var clock = System.Diagnostics.Stopwatch.StartNew();
        var lastReport = DateTime.UtcNow;
        Result outcome = Error.Of(ErrorCode.Internal, "disk restore did not complete");
        try
        {
            var password = o.PasswordProvider?.Invoke();
            int workers = Math.Clamp(Environment.ProcessorCount, 1, 8);
            var workerReaders = new List<IImageReader>();
            foreach (var imageFile in plan.ChainImages)
            {
                var first = await OrfImage.OpenAsync(imageFile, password).ConfigureAwait(false);
                if (!first.IsSuccess) { outcome = Result.Fail(first.Error); return outcome; }
                readers.Add(first.Value);
            }
            for (int w = 0; w < workers; w++)
            {
                IImageReader workerReader;
                if (w == 0) workerReader = readers[0];
                else
                {
                    var openedReader = await OrfImage.OpenAsync(plan.ChainImages[0], password).ConfigureAwait(false);
                    if (!openedReader.IsSuccess) { outcome = Result.Fail(openedReader.Error); return outcome; }
                    workerReader = openedReader.Value;
                    readers.Add(workerReader);
                }
                workerReaders.Add(workerReader);
            }

            var channel = Channel.CreateBounded<(int Idx, RestoreRegion Region)>(
                new BoundedChannelOptions(workers * 2) { SingleWriter = true });
            var inflight = new SemaphoreSlim(workers * 4, workers * 4);
            var results = new System.Collections.Concurrent.ConcurrentDictionary<int, byte[]>();
            var workerTasks = Enumerable.Range(0, workers).Select(w => Task.Run(async () =>
            {
                var reader = workerReaders[w];
                await foreach (var (idx, region) in channel.Reader.ReadAllAsync(ct).ConfigureAwait(false))
                {
                    var chunk = await reader.ReadChunkAsync(region.ChunkIndex, ct).ConfigureAwait(false);
                    if (!chunk.IsSuccess) throw new InvalidOperationException($"chunk {region.ChunkIndex}: {chunk.Error.Message}");
                    results[idx] = chunk.Value;
                }
            }, ct)).ToArray();

            for (int i = 0; i < plan.Regions.Count; i++)
            {
                ct.ThrowIfCancellationRequested();
                var region = plan.Regions[i];
                bool isSparse = region.FromImageFile == Restorer.ZeroFillSource;
                byte[] payload;
                if (isSparse && o.SkipUnwrittenRegions)
                {
                    skippedSparse++;
                    continue;
                }
                if (isSparse)
                {
                    payload = new byte[region.Length];
                }
                else
                {
                    await inflight.WaitAsync(ct).ConfigureAwait(false);
                    await channel.Writer.WriteAsync((i, region), ct).ConfigureAwait(false);
                    while (!results.TryRemove(i, out payload!)) await Task.Delay(1, ct).ConfigureAwait(false);
                    inflight.Release();
                }
                target.Seek(region.Offset, SeekOrigin.Begin);
                while (true)
                {
                    try
                    {
                        await target.WriteAsync(payload.AsMemory(), ct).ConfigureAwait(false);
                        break;
                    }
                    catch (UnauthorizedAccessException)
                    {
                        // Windows denies direct writes into sectors of a MOUNTED volume; a freshly
                        // attached target holds short-lived system handles, so dismount-and-retry
                        // until they are released (bounded, then fail honestly)
                        denyRetries++;
                        if (denyRetries > 60) throw;
                        await DismountVolumesOfDiskAsync(diskNumber, log).ConfigureAwait(false);
                        await Task.Delay(500, ct).ConfigureAwait(false);
                        target.Seek(region.Offset, SeekOrigin.Begin);
                    }
                }
                written += region.Length;

                var now = DateTime.UtcNow;
                if (progress is not null && (now - lastReport).TotalMilliseconds > 250)
                {
                    lastReport = now;
                    var speed = clock.Elapsed.TotalSeconds > 0.5 ? written / clock.Elapsed.TotalSeconds : 0;
                    progress.Report(new ProgressInfo("restoring-disk", written, plan.TargetSizeBytes, i + 1, plan.Regions.Count, speed));
                }
            }
            channel.Writer.TryComplete();
            await Task.WhenAll(workerTasks).ConfigureAwait(false);
            // always report the final numbers — fast passes (skip-unwritten on small targets)
            // can finish entirely inside the 250 ms throttle window and otherwise report nothing
            progress?.Report(new ProgressInfo("restoring-disk", written, plan.TargetSizeBytes, plan.Regions.Count, plan.Regions.Count, clock.Elapsed.TotalSeconds > 0.5 ? written / clock.Elapsed.TotalSeconds : 0));
            await target.FlushAsync(ct).ConfigureAwait(false);
            log.Write(LogLevel.Info, "restore", $"write pass done: {written} bytes written, {skippedSparse} sparse region(s) skipped, {clock.Elapsed}");

            if (o.VerifyAfterWrite)
            {
                // release the exclusive write handle before re-opening for the verify pass
                target.Dispose();
                // a service can re-open a dismounted volume BY GUID and NTFS then writes metadata
                // (usa fixups) behind our back — kick it off again right before verifying
                await DismountVolumesOfDiskAsync(diskNumber, log).ConfigureAwait(false);
                if (o.SkipUnwrittenRegions)
                    log.Write(LogLevel.Info, "restore", "verify pass checks written regions only (sparse regions skipped by option)");
                var verified = await VerifyWrittenAsync(plan, diskNumber, o, ct).ConfigureAwait(false);
                if (!verified.IsSuccess && verified.Error.Code == ErrorCode.RegionMismatch)
                {
                    // one honest retry: re-dismount, let in-flight third-party writes settle, re-read
                    await DismountVolumesOfDiskAsync(diskNumber, log).ConfigureAwait(false);
                    await Task.Delay(2000, ct).ConfigureAwait(false);
                    verified = await VerifyWrittenAsync(plan, diskNumber, o, ct).ConfigureAwait(false);
                }
                if (!verified.IsSuccess) { outcome = Result.Fail(verified.Error); return outcome; }
                log.Write(LogLevel.Info, "restore", $"verify pass OK ({clock.Elapsed} total)");
            }
            outcome = Result.Ok();
            return outcome;
        }
        catch (OperationCanceledException)
        {
            outcome = Error.Of(ErrorCode.Cancelled, "disk restore cancelled (a raw device restore interrupted mid-way leaves the disk inconsistent; re-run the restore)");
            return outcome;
        }
        catch (InvalidOperationException ex)
        {
            outcome = Error.Of(ErrorCode.CorruptChunk, "image read failed during disk restore", ex.Message);
            return outcome;
        }
        catch (IOException ex)
        {
            outcome = Error.Of(ErrorCode.DestinationFull, "device write failed during disk restore", ex.Message);
            return outcome;
        }
        finally
        {
            foreach (var r in readers) await r.DisposeAsync().ConfigureAwait(false);
        }
    }

    /// <summary>Second pass: re-open the device read-only and check every region against its
    /// expected content (stored hash, or zeros for explicit sparse regions).</summary>
    private static async Task<Result> VerifyWrittenAsync(RestorePlan plan, int diskNumber, DiskRestoreOptions o, CancellationToken ct)
    {
        var readHandle = Win32.Open(DiskLayout.DevicePrefix + diskNumber, write: false, exclusive: false);
        if (!readHandle.IsSuccess) return Result.Fail(readHandle.Error);
        using var device = readHandle.Value;
        var fs = new FileStream(device, FileAccess.Read, 1 << 16, isAsync: true);
        await using var _ = fs;
        var readers = new Dictionary<string, IImageReader>();
        try
        {
            foreach (var region in plan.Regions)
            {
                ct.ThrowIfCancellationRequested();
                bool isSparse = region.FromImageFile == Restorer.ZeroFillSource;
                if (isSparse && o.SkipUnwrittenRegions) continue;
                var buf = new byte[region.Length];
                fs.Seek(region.Offset, SeekOrigin.Begin);
                await fs.ReadExactlyAsync(buf, ct).ConfigureAwait(false);
                if (isSparse)
                {
                    foreach (var b in buf)
                        if (b != 0) return Error.Of(ErrorCode.RegionMismatch, $"post-write verify: region at {region.Offset} was expected zero");
                    continue;
                }
                if (!readers.TryGetValue(region.FromImageFile, out var reader))
                {
                    var opened = await OrfImage.OpenAsync(region.FromImageFile, o.PasswordProvider?.Invoke()).ConfigureAwait(false);
                    if (!opened.IsSuccess) return Result.Fail(opened.Error);
                    reader = opened.Value;
                    readers[region.FromImageFile] = reader;
                }
                var expected = await reader.GetChunkHashAsync(region.ChunkIndex, ct).ConfigureAwait(false);
                if (!expected.IsSuccess) return Result.Fail(expected.Error);
                var expectedData = await reader.ReadChunkAsync(region.ChunkIndex, ct).ConfigureAwait(false);
                if (!expectedData.IsSuccess) return Result.Fail(expectedData.Error);
                var actual = System.Security.Cryptography.SHA256.HashData(buf);
                if (!actual.AsSpan().SequenceEqual(expected.Value))
                {
                    var exp = expectedData.Value;
                    int diff = -1, diffCount = 0;
                    for (int b = 0; b < buf.Length && b < exp.Length; b++)
                        if (buf[b] != exp[b]) { if (diff < 0) diff = b; diffCount++; }
                    string ctx(byte[] a, int i) => Convert.ToHexString(a.AsSpan(Math.Max(0, i - 8), Math.Min(16, a.Length - Math.Max(0, i - 8))));
                    string where = diff >= 0
                        ? $"first diff at +{diff} diffs={diffCount} dev={ctx(buf, diff)} img={ctx(exp, diff)}"
                        : $"lengths: device={buf.Length} image={exp.Length}";
                    return Error.Of(ErrorCode.RegionMismatch,
                        $"post-write verify: SHA-256 mismatch for region at {region.Offset} chunk={region.ChunkIndex} len={region.Length} src={region.FromImageFile}: {where}");
                }
            }
            return Result.Ok();
        }
        finally
        {
            foreach (var r in readers.Values) await r.DisposeAsync().ConfigureAwait(false);
        }
    }

    /// <summary>Best-effort FSCTL_DISMOUNT on every volume of the disk (never throws). A freshly
    /// mounted volume usually has short-lived system handles (e.g. the SVI housekeeping writer), so
    /// the dismount is retried until it sticks. Returns false when any volume could NOT be
    /// dismounted — writing into a live volume corrupts both ways (deny + FS write-back), so the
    /// caller must refuse to write. No volumes at all (blank/fresh disk) counts as success.</summary>
    private static async Task<bool> DismountVolumesOfDiskAsync(int diskNumber, ILogSink log)
    {
        var volumes = VolumeEnumerator.ForDisk((uint)diskNumber);
        if (!volumes.IsSuccess) return true;
        var allOk = true;
        foreach (var v in volumes.Value)
        {
            // deleting the mount points matters as much as the dismount: Windows denies direct
            // disk writes into any volume still registered with the mount manager, even after a
            // successful FSCTL_DISMOUNT
            foreach (var mp in v.MountPaths)
            {
                var normalized = mp.EndsWith('\\') ? mp : mp + "\\";
                if (!Win32.DeleteVolumeMountPointW(normalized))
                    log.Write(LogLevel.Warn, "restore", $"delete mount point {mp} failed: error {System.Runtime.InteropServices.Marshal.GetLastWin32Error()}");
                else
                    log.Write(LogLevel.Info, "restore", $"deleted mount point {mp}");
            }
            var dismounted = false;
            for (int attempt = 1; attempt <= 30 && !dismounted; attempt++)
            {
                try
                {
                    var h = Win32.Open(v.VolumeGuidPath.TrimEnd('\\'), write: true, exclusive: false);
                    if (!h.IsSuccess) { await Task.Delay(500).ConfigureAwait(false); continue; }
                    using (h.Value)
                    {
                        // IOCTL_VOLUME_OFFLINE = dismount + mount-manager holds it unmounted,
                        // so raw disk writes into its range are no longer denied and nothing
                        // remounts it behind our back
                        var ok = Win32.IoControlRaw(h.Value, Win32.IOCTL_VOLUME_OFFLINE, 0);
                        if (ok.IsSuccess) { log.Write(LogLevel.Info, "restore", $"offline {v.VolumeGuidPath}: ok (attempt {attempt})"); dismounted = true; }
                        else if (attempt == 30) log.Write(LogLevel.Warn, "restore", $"offline {v.VolumeGuidPath} still failing: {ok.Error.Message}");
                    }
                }
                catch (Exception ex) { log.Write(LogLevel.Warn, "restore", $"dismount {v.VolumeGuidPath} failed: {ex.Message}"); break; }
                if (!dismounted) await Task.Delay(500).ConfigureAwait(false);
            }
            if (!dismounted) allOk = false;
        }
        return allOk;
    }
}
