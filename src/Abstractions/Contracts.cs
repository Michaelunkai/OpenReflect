namespace OpenReflect.Core.Abstractions;

/// <summary>Snapshot seam (D11). Real VSS provider lives in Core; tests use fakes. The session exposes a consistent read stream.</summary>
public interface ISnapshotProvider
{
    Task<Result<ISnapshotSession>> AcquireAsync(string sourcePath, bool allowRawFallback, CancellationToken ct = default);
}

public interface ISnapshotSession : IAsyncDisposable
{
    /// <summary>A seekable, read-only stream over the source (device, volume, or plain file).</summary>
    Stream OpenReadStream();
    string Description { get; }
    /// <summary>Byte ranges of the source that actually exist in this stream (intelligent sector).
    /// null = the whole source is backed; chunks fully outside every range are skipped without I/O
    /// and restore as zeros (or are skipped with SkipUnwrittenRegions).</summary>
    IReadOnlyList<BackingRange>? BackedRanges => null;
    /// <summary>Byte ranges whose content is MEANINGLESS on restore (pagefile/hiberfil/swapfile
    /// extents). Chunks fully inside a range are never read or stored — they restore as zeros,
    /// exactly like unallocated space (Macrium exclusion parity). Ranges are source-byte
    /// coordinates; a chunk that merely overlaps a range is still stored normally.</summary>
    IReadOnlyList<BackingRange> ZeroFillRanges => Array.Empty<BackingRange>();
    /// <summary>True when OpenReadStream() may be called repeatedly to obtain INDEPENDENT,
    /// separately-seekable streams over the same consistent source (parallel readers). When false,
    /// callers must use the single instance and read sequentially.</summary>
    bool SupportsIndependentStreams => false;
    /// <summary>An independent stream over the same source when SupportsIndependentStreams is
    /// true; null otherwise. Ownership passes to the caller, which must dispose it.</summary>
    Stream? TryOpenIndependentStream() => null;
}

/// <summary>A contiguous backed range of the source, in source byte coordinates.</summary>
public readonly record struct BackingRange(long Offset, long Length);

/// <summary>Format-layer writer. Format owns hashing, compression, encryption, framing, finalize ordering (D5).</summary>
public interface IImageWriter : IAsyncDisposable
{
    Task<Result> AppendChunkAsync(long chunkIndex, ReadOnlyMemory<byte> plaintext, CancellationToken ct = default);
    /// <summary>Finalize: index → manifest → footer → clear dirty flag, each flushed (D5).</summary>
    Task<Result<ImageHeaderInfo>> CommitAsync(CancellationToken ct = default);
    /// <summary>Abort: leave the file with dirty-shutdown flag set; footer never written (D5).</summary>
    Task AbandonAsync();
}

/// <summary>Format-layer reader over one .orf file (chain walking lives in Restore/Core).</summary>
public interface IImageReader : IAsyncDisposable
{
    ImageHeaderInfo Header { get; }
    string FilePath { get; }
    /// <summary>True when this image stores the logical chunk (used-sector imaging may omit regions).</summary>
    bool HasChunk(long chunkIndex);
    /// <summary>The stored plaintext SHA-256 for a logical chunk (FORMAT.md section 3), without reading the payload.</summary>
    Task<Result<byte[]>> GetChunkHashAsync(long chunkIndex, CancellationToken ct = default);
    /// <summary>Plaintext chunk bytes (decompressed, decrypted, hash-verified).</summary>
    Task<Result<byte[]>> ReadChunkAsync(long chunkIndex, CancellationToken ct = default);
    Task<Result<long>> GetChunkFileOffsetAsync(long chunkIndex, CancellationToken ct = default);
}

/// <summary>The engine. Source may be a device path (\\.\PhysicalDriveN, \\.\C:), a volume path (C:\… unmounted) or a plain file — files enable full pipeline testing without disks.</summary>
public interface IImageEngine
{
    Task<Result<ImageHeaderInfo>> CreateFullAsync(string sourcePath, string destFile, ImageOptions options, IProgress<ProgressInfo>? progress = null, CancellationToken ct = default);
    Task<Result<ImageHeaderInfo>> CreateIncrementalAsync(string sourcePath, string parentImageFile, string destFile, ImageOptions options, IProgress<ProgressInfo>? progress = null, CancellationToken ct = default);
    Task<Result<ImageHeaderInfo>> CreateDifferentialAsync(string sourcePath, string rootImageFile, string destFile, ImageOptions options, IProgress<ProgressInfo>? progress = null, CancellationToken ct = default);
}

public interface IVerifier
{
    Task<VerifyResult> VerifyAsync(string imageFile, VerifyDepth depth = VerifyDepth.Full, IProgress<ProgressInfo>? progress = null, Func<string?>? passwordProvider = null, CancellationToken ct = default);
}

public interface IRestorer
{
    /// <summary>Plan reads the chain and computes regions; never writes. Works for file targets and device targets.</summary>
    Task<Result<RestorePlan>> PlanAsync(string imageFile, string targetPath, Func<string?>? passwordProvider = null, CancellationToken ct = default);
    /// <summary>Executes a plan. Refuses unless DryRun==false AND Confirmed==true (D12); additional target checks inside.</summary>
    Task<Result> ExecuteAsync(RestorePlan plan, RestoreOptions options, IProgress<ProgressInfo>? progress = null, CancellationToken ct = default);
}

public interface IScheduler
{
    Task<Result> RegisterAsync(JobScheduleSpec spec, CancellationToken ct = default);
    Task<Result<IReadOnlyList<JobScheduleSpec>>> ListAsync(CancellationToken ct = default);
    Task<Result> DeleteAsync(string name, CancellationToken ct = default);
    Task<Result> RunOnceAsync(string name, CancellationToken ct = default);
}
