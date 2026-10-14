using OpenReflect.Core.Abstractions;

namespace OpenReflect.DiskAccess;

/// <summary>The intelligent-sector disk source. For \\.\PhysicalDriveN it produces a
/// MultiExtentStream whose backed ranges are: first/last 1 MiB (GPT), every partition; VSS-snapshotted
/// NTFS volumes are trimmed to the allocation state recorded in the snapshot's own $Bitmap.
/// Data for snapshotted volumes reads from the shadow device (crash-consistent), everything else
/// reads directly from the disk. Fallback chain: VSS requested → on VssFailure use direct reads
/// with whole-partition backing (caller decides whether that is acceptable for a live system disk).</summary>
public sealed class DiskSnapshotProvider : ISnapshotProvider
{
    private readonly bool _preferVss;
    private readonly bool _intelligent;
    private readonly ILogSink? _log;
    public const int GapToleranceBytes = 4 << 20;
    public const int MarginBytes = 1 << 20;

    public DiskSnapshotProvider(bool preferVss = true, bool intelligent = true, ILogSink? log = null)
    {
        _preferVss = preferVss; _intelligent = intelligent; _log = log;
    }

    public static int? ParseDiskNumber(string sourcePath)
    {
        if (sourcePath.StartsWith(DiskLayout.DevicePrefix, StringComparison.OrdinalIgnoreCase)
            && int.TryParse(sourcePath[DiskLayout.DevicePrefix.Length..], out var n) && n >= 0) return n;
        if (int.TryParse(sourcePath, out var bare) && bare >= 0) return bare;
        return null;
    }

    public async Task<Result<ISnapshotSession>> AcquireAsync(string sourcePath, bool allowRawFallback, CancellationToken ct = default)
    {
        var diskNumber = ParseDiskNumber(sourcePath);
        if (diskNumber is null)
            return Error.Of(ErrorCode.InvalidArgument, $"DiskSnapshotProvider needs \\\\.{DiskLayout.DevicePrefix[4..]}N or a disk number, got: {sourcePath}");
        var layout = await DiskLayout.ReadAsync(diskNumber.Value, ct).ConfigureAwait(false);
        if (!layout.IsSuccess) return Result<ISnapshotSession>.Fail(layout.Error);
        var disk = layout.Value;

        // ---- simple mode: whole disk backed, one direct stream ----
        if (!_intelligent)
        {
            var fs = new FileStream(DiskLayout.DevicePrefix + disk.Number, FileMode.Open, FileAccess.Read, FileShare.ReadWrite, 1 << 16, useAsync: true);
            return new DirectSession(fs, $"disk {disk.Number} (whole-disk)", BackedRanges: null);
        }

        // ---- intelligent mode ----
        var extents = new List<MultiExtentStream.Extent>();
        var backed = new List<BackingRange>();
        var disposables = new List<IAsyncDisposable>();
        VssSnapshotSet? vss = null;

        long size = disk.SizeBytes;
        long firstPartitionOffset = disk.Partitions.Count > 0 ? disk.Partitions.Min(p => p.Offset) : size;
        long lastPartitionEnd = disk.Partitions.Count > 0 ? disk.Partitions.Max(p => p.Offset + p.Length) : 0;
        long head = Math.Min(MarginBytes, Math.Min(size, firstPartitionOffset));
        FileStream DirectDiskStream()
        {
            var s = new FileStream(DiskLayout.DevicePrefix + disk.Number, FileMode.Open, FileAccess.Read, FileShare.ReadWrite, 1 << 16, useAsync: true);
            disposables.Add(s);
            return s;
        }

        if (head > 0)
        {
            extents.Add(new MultiExtentStream.Extent(0, head, $"disk {disk.Number} head (GPT/MBR)", DirectDiskStream));
            backed.Add(new BackingRange(0, head));
        }

        var volumes = VolumeEnumerator.ForDisk((uint)disk.Number);
        var volumeByPartition = new Dictionary<uint, VolumeRef>();
        if (volumes.IsSuccess)
            foreach (var v in volumes.Value)
                if (v.PartitionNumber > 0) volumeByPartition.TryAdd(v.PartitionNumber, v);

        if (_preferVss)
        {
            var ntfsVolumes = disk.Partitions
                .Where(p => volumeByPartition.TryGetValue((uint)p.Number, out var v) && v.FileSystem == "NTFS")
                .Select(p => volumeByPartition[(uint)p.Number].VolumeGuidPath)
                .Distinct(StringComparer.OrdinalIgnoreCase).ToList();
            if (ntfsVolumes.Count > 0)
            {
                var created = VssSnapshotSet.Create(ntfsVolumes, _log);
                if (created.IsSuccess) { vss = created.Value; disposables.Add(vss); }
                else _log?.Write(LogLevel.Warn, "vss", $"VSS unavailable ({created.Error.Message}); falling back to direct reads of live volumes");
            }
        }

        foreach (var p in disk.Partitions)
        {
            VolumeRef? vol = volumeByPartition.TryGetValue((uint)p.Number, out var v) ? v : null;
            string? shadow = vss is not null && vol is not null && vol.FileSystem == "NTFS"
                ? vss.ShadowDevices.GetValueOrDefault(vol.VolumeGuidPath) : null;

            if (!string.IsNullOrEmpty(shadow))
            {
                var shadowPath = shadow;
                long partOffset = p.Offset;
                List<BackingRange> trimmed;
                long coverLen;
                try
                {
                    // probe the shadow once for its $Bitmap (the stream below reopens it lazily)
                    using var probe = ShadowStream(shadowPath);
                    var alloc = await NtfsAllocation.ReadAsync(probe, p.Length, ct).ConfigureAwait(false);
                    if (!alloc.IsSuccess) throw new IOException(alloc.Error.Message);
                    trimmed = alloc.Value.AllocatedRanges(GapToleranceBytes)
                        .Select(r => new BackingRange(partOffset + Math.Min(r.Offset, p.Length), Math.Min(r.Length, p.Length - Math.Min(r.Offset, p.Length))))
                        .Where(r => r.Length > 0).ToList();
                    // an NTFS volume can be smaller than its GPT partition (backup-boot-sector
                    // slack) and the shadow device clips large reads at the volume end while
                    // small reads past it still return data — so the boot sector's own volume
                    // size is the only trustworthy coverLen; everything past it reads direct
                    coverLen = Math.Min(p.Length, alloc.Value.TotalClusters * alloc.Value.BytesPerCluster);
                    _log?.Write(LogLevel.Info, "intelligent", $"partition {p.Number} ({vol!.FileSystem}): {trimmed.Count} allocated run(s), {trimmed.Sum(r => r.Length) / (1024.0 * 1024):F0} MiB of {p.Length / (1024.0 * 1024):F0} MiB, shadow covers {coverLen / (1024.0 * 1024):F0} of {p.Length / (1024.0 * 1024):F0} MiB");
                }
                catch (Exception ex)
                {
                    if (Environment.GetEnvironmentVariable("ORF_DEBUG") == "1")
                        Console.Error.WriteLine($"##PARSEDBG partition {p.Number}: {ex.GetType().Name}: {ex.Message}");
                    _log?.Write(LogLevel.Warn, "intelligent", $"partition {p.Number}: $Bitmap parse failed ({ex.Message}); backing whole partition");
                    trimmed = new List<BackingRange> { new(partOffset, p.Length) };
                    coverLen = p.Length;
                }
                extents.Add(new MultiExtentStream.Extent(p.Offset, coverLen, $"shadow {p.Number}", () => ShadowStream(shadowPath)));
                if (coverLen < p.Length)
                    extents.Add(new MultiExtentStream.Extent(p.Offset + coverLen, p.Length - coverLen, $"disk {disk.Number} partition {p.Number} (volume slack)", DirectDiskStream));
                backed.AddRange(trimmed.Where(r => r.Offset + r.Length <= partOffset + coverLen));
            }
            else
            {
                extents.Add(new MultiExtentStream.Extent(p.Offset, p.Length, $"disk {disk.Number} partition {p.Number}", DirectDiskStream));
                backed.Add(new BackingRange(p.Offset, p.Length));
            }
        }

        // the tail extent must cover EVERYTHING after the last partition, not just the last
        // 1 MiB: the chunk-grid expansion of the last partition's ranges claims chunks past its
        // end, and the gap before the backup GPT must be readable (direct) for those chunks
        long tailStart = disk.Partitions.Count > 0 ? lastPartitionEnd : size - Math.Min(MarginBytes, size);
        if (tailStart < size)
        {
            extents.Add(new MultiExtentStream.Extent(tailStart, size - tailStart, $"disk {disk.Number} tail (gap + backup GPT)", DirectDiskStream));
            backed.Add(new BackingRange(tailStart, size - tailStart));
        }

        var stream = new MultiExtentStream(extents, size);
        return new DiskSession(stream, $"disk {disk.Number} (intelligent{(vss is not null ? "+VSS" : "")})", backed, disposables);
    }

    /// <summary>Opens a shadow device for streaming; overlapped first, plain sync handle as
    /// fallback (CreateFileW on GLOBALROOT devices can transiently fail with a parameter error).</summary>
    private static Stream ShadowStream(string shadowPath)
    {
        try { return new FileStream(shadowPath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite, 1 << 16, useAsync: true); }
        catch (IOException) { return new FileStream(shadowPath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite, 1 << 16, useAsync: false); }
    }

    private sealed class DirectSession : ISnapshotSession
    {
        private readonly FileStream _fs;
        public DirectSession(FileStream fs, string description, IReadOnlyList<BackingRange>? BackedRanges)
        {
            _fs = fs; Description = description; this.BackedRanges = BackedRanges;
        }
        public string Description { get; }
        public IReadOnlyList<BackingRange>? BackedRanges { get; }
        public Stream OpenReadStream() => _fs;
        public async ValueTask DisposeAsync() => await _fs.DisposeAsync().ConfigureAwait(false);
    }

    private sealed class DiskSession : ISnapshotSession
    {
        private readonly MultiExtentStream _stream;
        private readonly List<IAsyncDisposable> _disposables;
        public DiskSession(MultiExtentStream stream, string description, List<BackingRange> backed, List<IAsyncDisposable> disposables)
        {
            _stream = stream; Description = description; BackedRanges = backed; _disposables = disposables;
        }
        public string Description { get; }
        public IReadOnlyList<BackingRange>? BackedRanges { get; }
        public Stream OpenReadStream() => _stream;
        public async ValueTask DisposeAsync()
        {
            await _stream.DisposeAsync().ConfigureAwait(false);
            foreach (var d in _disposables)
            {
                try { await d.DisposeAsync().ConfigureAwait(false); }
                catch { /* best-effort: VSS cleanup must never mask the imaging result */ }
            }
        }
    }
}
