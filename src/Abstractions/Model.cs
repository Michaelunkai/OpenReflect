namespace OpenReflect.Core.Abstractions;

public enum ChainKind { Full = 0, Incremental = 1, Differential = 2 }
public enum CompressionKind { None = 0, Deflate = 1, Zstd = 2 }
public enum EncryptionKind { None = 0, Aes256Gcm = 1 }

/// <summary>Options for creating an image. Passwords are supplied via <see cref="PasswordProvider"/> — never stored (D10).</summary>
public sealed record ImageOptions
{
    public long ChunkSizeBytes { get; init; } = 4 * 1024 * 1024;
    public CompressionKind Compression { get; init; } = CompressionKind.Deflate;
    public int CompressionLevel { get; init; } = 6;          // 1..9 for deflate
    public EncryptionKind Encryption { get; init; } = EncryptionKind.None;
    public int KdfIterations { get; init; } = 600000;        // PBKDF2-HMAC-SHA256
    public Func<string?>? PasswordProvider { get; init; }    // returns the password when encryption requested
    public string? SourceLabel { get; init; }
    /// <summary>Used-sector imaging: all-zero regions are omitted from the image (restore writes zeros).</summary>
    public bool SkipZeroRegions { get; init; } = true;
    public ILogSink? Log { get; init; }
}

/// <summary>Throttled progress (D7: at most every 250 ms or 64 chunks).</summary>
public sealed record ProgressInfo(string Phase, long ProcessedBytes, long TotalBytes, int ChunksDone, int ChunksTotal, double SpeedBytesPerSec);

/// <summary>Header facts surfaced by readers.</summary>
public sealed record ImageHeaderInfo(
    Guid ImageId,
    Guid ParentImageId,
    ChainKind ChainKind,
    long ChunkSizeBytes,
    bool Encrypted,
    bool IsComplete,
    long ChunkCount,
    long LogicalSizeBytes,
    string SourceLabel,
    DateTimeOffset CreatedAt);

public sealed record ChunkError(long ChunkIndex, string Reason);

public enum VerifyDepth { Structure = 0, Manifest = 1, Full = 2 }

public sealed record VerifyResult(
    bool Success,
    string ImagePath,
    bool IsComplete,
    long ChunksChecked,
    IReadOnlyList<ChunkError> FailedChunks,
    Error? Error = null)
{
    public static VerifyResult Ok(string path, long chunks) => new(true, path, true, chunks, Array.Empty<ChunkError>());
    public static VerifyResult Fail(string path, Error error) => new(false, path, false, 0, Array.Empty<ChunkError>(), error);
}

/// <summary>Restore safety contract (D12). Writes happen only when <see cref="Confirmed"/> is true and <see cref="DryRun"/> false.</summary>
public sealed record RestoreOptions
{
    public bool DryRun { get; init; } = true;
    public bool Confirmed { get; init; } = false;
    public bool AllowOverwriteSystemDisk { get; init; } = false;
    /// <summary>Supplies the password for encrypted images (D10). Consumed at open time only.</summary>
    public Func<string?>? PasswordProvider { get; init; }
    public ILogSink? Log { get; init; }
}

public sealed record RestoreRegion(long Offset, long Length, string FromImageFile, long ChunkIndex);

public sealed record RestorePlan(
    string TargetPath,
    long TargetSizeBytes,
    IReadOnlyList<RestoreRegion> Regions,
    IReadOnlyList<string> ChainImages)
{
    public long BytesToWrite => Regions.Sum(r => r.Length);
}

/// <summary>Windows Task Scheduler registration spec (Schedule module wraps schtasks.exe).</summary>
public sealed record JobScheduleSpec(string Name, string CommandLine, string WorkingDirectory, ScheduleKind Kind, string TimeOfDay, IReadOnlyList<string>? DaysOfWeek = null);

public enum ScheduleKind { Disabled, Daily, Weekly, Monthly, Logon }
