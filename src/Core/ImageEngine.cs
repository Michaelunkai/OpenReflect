using System.Diagnostics;
using System.Security.Cryptography;
using OpenReflect.Core.Abstractions;
using OpenReflect.Format;

namespace OpenReflect.Core;

/// <summary>The imaging engine: snapshot → region decision (zero-skip / parent-dedup) → format writer.
/// Sequential pipeline in v0.1 (ADR-13); Cancellation converts to Result.Cancelled at this boundary.</summary>
public sealed class ImageEngine : IImageEngine
{
    private readonly ISnapshotProvider _snapshots;
    public ImageEngine(ISnapshotProvider? snapshots = null) => _snapshots = snapshots ?? new PassThroughSnapshotProvider();

    public Task<Result<ImageHeaderInfo>> CreateFullAsync(string sourcePath, string destFile, ImageOptions options, IProgress<ProgressInfo>? progress = null, CancellationToken ct = default)
        => RunAsync(sourcePath, destFile, options, ChainKind.Full, refFile: null, progress, ct);

    public Task<Result<ImageHeaderInfo>> CreateIncrementalAsync(string sourcePath, string parentImageFile, string destFile, ImageOptions options, IProgress<ProgressInfo>? progress = null, CancellationToken ct = default)
        => RunAsync(sourcePath, destFile, options, ChainKind.Incremental, refFile: parentImageFile, progress, ct);

    public Task<Result<ImageHeaderInfo>> CreateDifferentialAsync(string sourcePath, string rootImageFile, string destFile, ImageOptions options, IProgress<ProgressInfo>? progress = null, CancellationToken ct = default)
        => RunAsync(sourcePath, destFile, options, ChainKind.Differential, refFile: rootImageFile, progress, ct);

    private static async Task<Result<IImageReader>> OpenReferenceAsync(string? refFile, CancellationToken ct)
    {
        if (refFile is null) return new NullRefReader();
        var opened = await OrfImage.OpenAsync(refFile).ConfigureAwait(false);
        if (!opened.IsSuccess) return Result<IImageReader>.Fail(opened.Error);
        if (!opened.Value.Header.IsComplete)
            return Error.Of(ErrorCode.ChainIncomplete, $"cannot extend a chain past an incomplete image: {refFile}");
        return opened.Value;
    }

    private sealed class NullRefReader : IImageReader
    {
        public ImageHeaderInfo Header => new(Guid.Empty, Guid.Empty, ChainKind.Full, 1, false, true, 0, 0, "", default);
        public string FilePath => "";
        public bool HasChunk(long chunkIndex) => false;
        public Task<Result<byte[]>> GetChunkHashAsync(long chunkIndex, CancellationToken ct = default) => Task.FromResult(Result<byte[]>.Fail(Error.Of(ErrorCode.BadFormat, "no reference")));
        public Task<Result<byte[]>> ReadChunkAsync(long chunkIndex, CancellationToken ct = default) => Task.FromResult(Result<byte[]>.Fail(Error.Of(ErrorCode.BadFormat, "no reference")));
        public Task<Result<long>> GetChunkFileOffsetAsync(long chunkIndex, CancellationToken ct = default) => Task.FromResult(Result<long>.Fail(Error.Of(ErrorCode.BadFormat, "no reference")));
        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }

    private async Task<Result<ImageHeaderInfo>> RunAsync(string sourcePath, string destFile, ImageOptions o, ChainKind kind, string? refFile, IProgress<ProgressInfo>? progress, CancellationToken ct)
    {
        var log = o.Log ?? NullLogSink.Instance;
        if (o.Compression == CompressionKind.Zstd) return Error.Of(ErrorCode.NotSupported, "zstd codec arrives in 0.2 (ADR-13)");

        string? password = null;
        if (o.Encryption == EncryptionKind.Aes256Gcm)
        {
            password = o.PasswordProvider?.Invoke();
            if (string.IsNullOrEmpty(password)) return Error.Of(ErrorCode.InvalidArgument, "encryption requested but no password provider supplied");
        }

        var reference = await OpenReferenceAsync(refFile, ct).ConfigureAwait(false);
        if (!reference.IsSuccess) return Result<ImageHeaderInfo>.Fail(reference.Error);
        var parent = reference.Value;

        var session = await _snapshots.AcquireAsync(sourcePath, allowRawFallback: true, ct).ConfigureAwait(false);
        if (!session.IsSuccess) return Result<ImageHeaderInfo>.Fail(session.Error);
        await using var src = session.Value;
        var stream = src.OpenReadStream();
        long totalSize;
        try { totalSize = stream.Length; }
        catch (Exception ex) { return Error.Of(ErrorCode.SourceReadError, "cannot determine source size", ex.Message); }

        long chunkSize = o.ChunkSizeBytes;
        long logicalChunks = (totalSize + chunkSize - 1) / chunkSize;
        log.Write(LogLevel.Info, "engine", $"start {kind}: source={sourcePath} size={totalSize} chunks={logicalChunks} ref={(refFile ?? "none")}");

        var spec = new OrfCreateSpec
        {
            ImageId = Guid.NewGuid(),
            ParentImageId = kind == ChainKind.Full ? Guid.Empty : parent.Header.ImageId,
            ChainKind = kind,
            ChunkSizeBytes = chunkSize,
            SourceLabel = o.SourceLabel ?? src.Description,
            Compression = o.Compression,
            CompressionLevel = o.CompressionLevel,
            Encryption = o.Encryption,
            KdfIterations = o.KdfIterations,
            Password = password,
            LogicalSizeBytes = totalSize,
        };
        var writer = await OrfImage.CreateAsync(destFile, spec, ct).ConfigureAwait(false);

        var clock = Stopwatch.StartNew();
        long bytesHashed = 0, chunksStored = 0, chunksSkipped = 0;
        var lastReport = DateTime.UtcNow;
        try
        {
            var buf = new byte[chunkSize];
            for (long logical = 0; logical < logicalChunks; logical++)
            {
                ct.ThrowIfCancellationRequested();
                long len = Math.Min(chunkSize, totalSize - logical * chunkSize);
                await ReadExactlyAsync(stream, logical * chunkSize, buf.AsMemory(0, (int)len), ct).ConfigureAwait(false);

                bool isZero = o.SkipZeroRegions && IsAllZero(buf.AsSpan(0, (int)len));
                bool store = !isZero;
                if (store && kind != ChainKind.Full && parent.HasChunk(logical))
                {
                    var refHash = await parent.GetChunkHashAsync(logical, ct).ConfigureAwait(false);
                    if (refHash.IsSuccess && refHash.Value.AsSpan().SequenceEqual(SHA256.HashData(buf.AsSpan(0, (int)len))))
                        store = false;   // unchanged region: inherited from parent chain (D2)
                }

                if (store)
                {
                    var appended = await writer.AppendChunkAsync(logical, buf.AsMemory(0, (int)len), ct).ConfigureAwait(false);
                    if (!appended.IsSuccess) return Result<ImageHeaderInfo>.Fail(appended.Error);
                    chunksStored++;
                }
                else chunksSkipped++;

                bytesHashed += len;
                var now = DateTime.UtcNow;
                if (progress is not null && (now - lastReport).TotalMilliseconds >= 250)
                {
                    lastReport = now;
                    var speed = clock.Elapsed.TotalSeconds > 0.5 ? bytesHashed / clock.Elapsed.TotalSeconds : 0;
                    progress.Report(new ProgressInfo("imaging", bytesHashed, totalSize, (int)(logical + 1), (int)logicalChunks, speed));
                }
            }

            var committed = await writer.CommitAsync(ct).ConfigureAwait(false);
            log.Write(LogLevel.Info, "engine", $"done: stored={chunksStored} skipped={chunksSkipped} bytes={bytesHashed} elapsed={clock.Elapsed}");
            return committed;
        }
        catch (OperationCanceledException)
        {
            await writer.AbandonAsync().ConfigureAwait(false);
            return Error.Of(ErrorCode.Cancelled, "job cancelled; partial image retains the dirty-shutdown flag (D5)");
        }
        catch
        {
            await writer.AbandonAsync().ConfigureAwait(false);   // D5: crash semantics on any fault
            throw;
        }
        finally
        {
            await parent.DisposeAsync().ConfigureAwait(false);
        }
    }

    private static async Task ReadExactlyAsync(Stream s, long at, Memory<byte> target, CancellationToken ct)
    {
        s.Seek(at, SeekOrigin.Begin);
        int done = 0;
        while (done < target.Length)
        {
            int n = await s.ReadAsync(target[done..], ct).ConfigureAwait(false);
            if (n == 0) throw new EndOfStreamException($"source ended at {at + done} of {target.Length + at} requested bytes");
            done += n;
        }
    }

    private static bool IsAllZero(ReadOnlySpan<byte> span)
    {
        foreach (var b in span) if (b != 0) return false;
        return true;
    }
}
