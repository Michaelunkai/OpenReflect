using OpenReflect.Core.Abstractions;

namespace OpenReflect.Core;

/// <summary>Direct read of the source (plain file or device path). This is the raw-read path:
/// correct for files, mounted VHDs and idle volumes; locked system volumes need VSS (0.2, RISKS R1).</summary>
public sealed class PassThroughSnapshotProvider : ISnapshotProvider
{
    public async Task<Result<ISnapshotSession>> AcquireAsync(string sourcePath, bool allowRawFallback, CancellationToken ct = default)
    {
        try
        {
            if (File.Exists(sourcePath))
            {
                var fs = new FileStream(sourcePath, FileMode.Open, FileAccess.Read, FileShare.Read, 1 << 16, useAsync: true);
                return new StreamSession(fs, sourcePath);
            }
            if (sourcePath.StartsWith(@"\\.\", StringComparison.OrdinalIgnoreCase) || sourcePath.EndsWith(':'))
            {
                var fs = new FileStream(sourcePath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite, 1 << 16, useAsync: true);
                return new StreamSession(fs, sourcePath);
            }
            return Error.Of(ErrorCode.SourceNotFound, $"source not found: {sourcePath}");
        }
        catch (UnauthorizedAccessException ex)
        {
            return Error.Of(ErrorCode.SourceAccessDenied, $"access denied reading {sourcePath} (elevation may be required)", ex.Message);
        }
        catch (IOException ex)
        {
            return Error.Of(ErrorCode.SourceReadError, $"cannot open {sourcePath}", ex.Message);
        }
    }

    private sealed class StreamSession : ISnapshotSession
    {
        private readonly FileStream _fs;
        public StreamSession(FileStream fs, string description) { _fs = fs; Description = description; }
        public string Description { get; }
        public Stream OpenReadStream() => _fs;
        public async ValueTask DisposeAsync() => await _fs.DisposeAsync().ConfigureAwait(false);
    }
}

/// <summary>VSS provider seam. The real AlphaVSS/vssapi integration is deferred to 0.2 (ADR-13,
/// RISKS R1); v0.1 records a typed VssFailure so callers can fall back explicitly.</summary>
public sealed class DeferredVssSnapshotProvider : ISnapshotProvider
{
    public Task<Result<ISnapshotSession>> AcquireAsync(string sourcePath, bool allowRawFallback, CancellationToken ct = default)
        => Task.FromResult(Result<ISnapshotSession>.Fail(
            Error.Of(ErrorCode.VssFailure, "VSS snapshots arrive in 0.2 (ADR-13); the raw-read path applies to files, mounted VHDs and idle volumes only")));
}

/// <summary>Pure provider-selection logic (unit-testable seam): prefer VSS when asked; fall back to
/// raw read only when explicitly allowed.</summary>
public static class SnapshotSelector
{
    public static (ISnapshotProvider Provider, bool UsedFallback) Select(
        bool sourceIsDevice, bool preferVss, ISnapshotProvider vss, ISnapshotProvider raw, bool allowRawFallback)
    {
        if (preferVss && sourceIsDevice)
        {
            if (allowRawFallback) return (raw, UsedFallback: true);
            return (vss, UsedFallback: false);
        }
        return (raw, UsedFallback: false);
    }
}
