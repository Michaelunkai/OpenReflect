using System.Diagnostics;
using System.Security.Cryptography;
using System.Threading.Channels;
using OpenReflect.Core.Abstractions;
using OpenReflect.Format;

namespace OpenReflect.Core;

/// <summary>The imaging engine: snapshot → region decision (backed-range skip / zero-skip /
/// parent-dedup) → format writer. Parallel pipeline (0.2, ADR-13b): one reader task, W transform
/// workers, and a single writer task that appends in strict logical order (format requires
/// ascending indices). Cancellation converts to Result.Cancelled at this boundary.</summary>
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

    /// <summary>Serializes parent hash lookups (parent readers own one file handle).</summary>
    private sealed class ParentGate
    {
        private readonly IImageReader _parent;
        private readonly SemaphoreSlim _lock = new(1, 1);
        public ParentGate(IImageReader parent) => _parent = parent;
        public bool HasChunk(long idx) => _parent.HasChunk(idx);
        public async Task<byte[]?> HashOfAsync(long idx, CancellationToken ct)
        {
            await _lock.WaitAsync(ct).ConfigureAwait(false);
            try
            {
                var h = await _parent.GetChunkHashAsync(idx, ct).ConfigureAwait(false);
                return h.IsSuccess ? h.Value : null;
            }
            finally
            {
                _lock.Release();
            }
        }
        public Task DisposeAsync() => _parent.DisposeAsync().AsTask();
    }

    /// <summary>One pipeline work item. The ordered writer consumes items by logical index from
    /// the results dictionary — no slot reuse, no shared task-completion state.</summary>
    private sealed class WorkItem
    {
        public long Index;
        public int Length;
        public byte[] Buffer = Array.Empty<byte>();
        public bool SkippedUnbacked;
        public bool ZeroFilled;
        public bool Store;
        public OrfWriter.PreparedChunk? Prepared;
        public Exception? Error;
    }

    private async Task<Result<ImageHeaderInfo>> RunAsync(string sourcePath, string destFile, ImageOptions o, ChainKind kind, string? refFile, IProgress<ProgressInfo>? progress, CancellationToken ct)
    {
        var log = o.Log ?? NullLogSink.Instance;

        string? password = null;
        if (o.Encryption == EncryptionKind.Aes256Gcm)
        {
            password = o.PasswordProvider?.Invoke();
            if (string.IsNullOrEmpty(password)) return Error.Of(ErrorCode.InvalidArgument, "encryption requested but no password provider supplied");
        }

        var reference = await OpenReferenceAsync(refFile, ct).ConfigureAwait(false);
        if (!reference.IsSuccess) return Result<ImageHeaderInfo>.Fail(reference.Error);
        var parent = new ParentGate(reference.Value);

        var session = await _snapshots.AcquireAsync(sourcePath, allowRawFallback: true, ct).ConfigureAwait(false);
        if (!session.IsSuccess) { await parent.DisposeAsync().ConfigureAwait(false); return Result<ImageHeaderInfo>.Fail(session.Error); }
        await using var src = session.Value;
        var stream = src.OpenReadStream();
        var backed = src.BackedRanges;
        long totalSize;
        try { totalSize = stream.Length; }
        catch (Exception ex) { return Error.Of(ErrorCode.SourceReadError, "cannot determine source size", ex.Message); }

        long chunkSize = o.ChunkSizeBytes;
        long logicalChunks = (totalSize + chunkSize - 1) / chunkSize;
        log.Write(LogLevel.Info, "engine", $"start {kind}: source={sourcePath} size={totalSize} chunks={logicalChunks} workers={Environment.ProcessorCount} ref={(refFile ?? "none")}");

        var spec = new OrfCreateSpec
        {
            ImageId = Guid.NewGuid(),
            ParentImageId = kind == ChainKind.Full ? Guid.Empty : reference.Value.Header.ImageId,
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
        long bytesHashed = 0, chunksStored = 0, chunksSkipped = 0, unbackedSkipped = 0, zeroFilledBytes = 0;
        var lastReport = DateTime.UtcNow;
        int workers = Math.Clamp(Environment.ProcessorCount, 2, 16);
        var dispatch = Channel.CreateBounded<WorkItem>(new BoundedChannelOptions(workers * 2)
        {
            SingleReader = false,
            SingleWriter = false,   // multiple readers dispatch concurrently
        });
        // watermark: readers may run at most (workers*4) chunks ahead of the ordered writer
        var inflight = new SemaphoreSlim(workers * 4, workers * 4);
        var results = new System.Collections.Concurrent.ConcurrentDictionary<long, WorkItem>();
        // Backed-range check must be CHUNK-SAFE: a chunk is backed when fully inside the UNION of
        // ranges, where ranges are first merged (adjacent/overlapping) then expanded outward to
        // chunk boundaries. Without the grid expansion, sub-chunk margins (GPT entry arrays!) and
        // non-chunk-aligned partition tails would be silently dropped from the image.
        var backedGrid = MergeAndExpandToChunkGrid(backed, chunkSize, totalSize);
        // zero-fill ranges (pagefile/hiberfil/swapfile): chunks FULLY inside are never read or
        // stored — they restore as zeros like unallocated space. Partial overlap stores normally.
        var zeroGrid = MergeAndExpandToChunkGrid(src.ZeroFillRanges, chunkSize, totalSize);
        bool IsBacked(long chunkIndex)
        {
            if (backedGrid is null) return true;
            long off = chunkIndex * chunkSize, end = Math.Min(off + chunkSize, totalSize);
            foreach (var r in backedGrid)
                if (r.Offset <= off && end <= r.Offset + r.Length) return true;
            return false;
        }
        bool IsZeroFilled(long chunkIndex)
        {
            if (zeroGrid is null) return false;
            long off = chunkIndex * chunkSize, end = Math.Min(off + chunkSize, totalSize);
            foreach (var r in zeroGrid)
                if (r.Offset <= off && end <= r.Offset + r.Length) return true;
            return false;
        }

        var streams = new List<Stream>();
        try
        {
            // transform workers: zero-check → parent dedup → prepare (parallel, order-free)
            Exception? workerFault = null;
            var workerTasks = Enumerable.Range(0, workers).Select(_ => Task.Run(async () =>
            {
                try
                {
                    await foreach (var item in dispatch.Reader.ReadAllAsync(ct).ConfigureAwait(false))
                    {
                        try
                        {
                            var span = item.Buffer.AsSpan(0, item.Length);
                            bool isZero = o.SkipZeroRegions && IsAllZero(span);
                            item.Store = !isZero;
                            if (item.Store && kind != ChainKind.Full && parent.HasChunk(item.Index))
                            {
                                var localHash = SHA256.HashData(span);
                                var refHash = await parent.HashOfAsync(item.Index, ct).ConfigureAwait(false);
                                if (refHash is not null && refHash.AsSpan().SequenceEqual(localHash))
                                    item.Store = false;   // unchanged region: inherited from parent chain (D2)
                                else
                                    item.Prepared = writer.PrepareChunk(item.Index, item.Buffer, localHash);
                            }
                            else if (item.Store)
                            {
                                item.Prepared = writer.PrepareChunk(item.Index, item.Buffer);
                            }
                        }
                        catch (Exception ex) { item.Error = ex; }
                        results[item.Index] = item;
                    }
                }
                catch (Exception ex) { Volatile.Write(ref workerFault, ex); }   // a dead worker must never swallow an in-flight chunk silently   // a dead worker must never swallow an in-flight chunk silently
            }, ct)).ToArray();

            // readers: R interleaved source readers (R=1 for single-stream sources) feed the
            // workers. Reader r handles chunks where chunkIndex % R == r; correctness never
            // depends on read order (the ordered writer sorts via the results map).
            int readerCount = src.SupportsIndependentStreams
                ? Math.Clamp(Environment.ProcessorCount / 2, 1, 12) : 1;
            streams.Add(stream);
            var readerTasks = new List<Task>();
            for (int r = 1; r < readerCount; r++)
            {
                var extra = src.TryOpenIndependentStream();
                if (extra is null) break;
                streams.Add(extra);
            }
            readerCount = streams.Count;
            log.Write(LogLevel.Info, "engine", $"readers={readerCount}");

            for (int r = 0; r < readerCount; r++)
            {
                var rs = streams[r];
                int readerId = r;   // capture a per-iteration copy: the for-loop variable keeps mutating (r ends at readerCount)
                readerTasks.Add(Task.Run(async () =>
                {
                    for (long logical = readerId; logical < logicalChunks; logical += readerCount)
                    {
                        await inflight.WaitAsync(ct).ConfigureAwait(false);
                        if (!IsBacked(logical))
                        {
                            unbackedSkipped++;
                            // release the watermark HERE: skip entries must never hold permits
                            // ahead of the writer, or the reader of the next backed chunk can be
                            // starved by 64 unconsumed skips past the writer's position (deadlock)
                            inflight.Release();
                            results[logical] = new WorkItem { Index = logical, Length = 0, Store = false, SkippedUnbacked = true };
                            continue;
                        }
                        long chunkLen = Math.Min(chunkSize, totalSize - logical * chunkSize);
                        if (IsZeroFilled(logical))
                        {
                            // meaningless content (pagefile/hiberfil/swapfile): no read, no store —
                            // the writer counts the bytes for progress and never appends a record
                            inflight.Release();
                            results[logical] = new WorkItem { Index = logical, Length = (int)chunkLen, Store = false, ZeroFilled = true };
                            continue;
                        }
                        var item = new WorkItem
                        {
                            Index = logical,
                            Length = (int)chunkLen,
                            Buffer = new byte[chunkLen],
                        };
                        await ReadExactlyAsync(rs, logical * chunkSize, item.Buffer, ct).ConfigureAwait(false);
                        await dispatch.Writer.WriteAsync(item, ct).ConfigureAwait(false);
                    }
                }, ct));
            }
            var allReaders = Task.WhenAll(readerTasks);
            _ = allReaders.ContinueWith(t => dispatch.Writer.TryComplete(), ct, TaskContinuationOptions.OnlyOnRanToCompletion, TaskScheduler.Default);

            // writer: append strictly in logical order as results arrive
            for (long logical = 0; logical < logicalChunks; logical++)
            {
                ct.ThrowIfCancellationRequested();
                WorkItem? item;
                long lastConsumeTick = Environment.TickCount64;
                while (!results.TryRemove(logical, out item))
                {
                    // readers complete when they have DISPATCHED everything, long before the
                    // workers drain the channel — only a reader FAULT means the chunk never comes
                    if (allReaders.Exception is not null)
                        throw new IOException($"reader faulted before chunk {logical} — pipeline fault",
                            allReaders.Exception.GetBaseException());
                    // a worker that died in-flight takes its chunk with it — surface that honestly
                    if (Volatile.Read(ref workerFault) is not null)
                        throw new IOException($"worker faulted before chunk {logical} — pipeline fault", workerFault);
                    // watchdog: no chunk may stall the ordered writer forever (wedged snapshot,
                    // dead worker) — fail the backup honestly instead of hanging without end
                    if (Environment.TickCount64 - lastConsumeTick > TimeSpan.FromMinutes(10).TotalMilliseconds)
                    {
                        var keys = results.Keys;
                        long min = keys.Count > 0 ? keys.Min() : -1, max = keys.Count > 0 ? keys.Max() : -1;
                        int below = 0; foreach (var k in keys) if (k < logical) below++;
                        log.Write(LogLevel.Error, "engine", $"stall-diagnostic: waiting={logical} resultsCount={keys.Count} below={below} min={min} max={max} inflightFree={inflight.CurrentCount} channelCount={dispatch.Reader.Count}");
                        var sample = keys.Where(k => k >= logical).OrderBy(k => k).Take(10);
                        log.Write(LogLevel.Error, "engine", $"stall-diagnostic: next keys: {string.Join(",", sample)}");
                        throw new IOException($"pipeline stalled before chunk {logical} for over 10 minutes — aborting rather than hanging");
                    }
                    await Task.Delay(50, ct).ConfigureAwait(false);
                }
                lastConsumeTick = Environment.TickCount64;
                var work = item!;
                if (work.Error is not null) throw work.Error;
                if (work.SkippedUnbacked) { continue; }   // permit already released by the reader
                if (work.ZeroFilled)
                {
                    zeroFilledBytes += work.Length;
                    chunksSkipped++;
                    bytesHashed += work.Length;
                    continue;
                }
                if (work.Store && work.Prepared is not null)
                {
                    var appended = await writer.AppendPreparedAsync(work.Index, work.Prepared, ct).ConfigureAwait(false);
                    if (!appended.IsSuccess) return Result<ImageHeaderInfo>.Fail(appended.Error);
                    chunksStored++;
                }
                else chunksSkipped++;
                bytesHashed += work.Length;
                inflight.Release();

                var now = DateTime.UtcNow;
                if (progress is not null && (now - lastReport).TotalMilliseconds >= 250)
                {
                    lastReport = now;
                    var speed = clock.Elapsed.TotalSeconds > 0.5 ? bytesHashed / clock.Elapsed.TotalSeconds : 0;
                    progress.Report(new ProgressInfo("imaging", bytesHashed, totalSize, (int)(logical + 1), (int)logicalChunks, speed));
                }
            }

            await allReaders.ConfigureAwait(false);
            await Task.WhenAll(workerTasks).ConfigureAwait(false);
            var committed = await writer.CommitAsync(ct).ConfigureAwait(false);
            log.Write(LogLevel.Info, "engine", $"done: stored={chunksStored} skipped={chunksSkipped} unbacked={unbackedSkipped} zeroFilled={zeroFilledBytes} bytes={bytesHashed} elapsed={clock.Elapsed}");
            return committed;
        }

        catch (OperationCanceledException)
        {
            dispatch.Writer.TryComplete();
            await writer.AbandonAsync().ConfigureAwait(false);
            return Error.Of(ErrorCode.Cancelled, "job cancelled; partial image retains the dirty-shutdown flag (D5)");
        }
        catch
        {
            dispatch.Writer.TryComplete();
            await writer.AbandonAsync().ConfigureAwait(false);   // D5: crash semantics on any fault
            throw;
        }
        finally
        {
            foreach (var s in streams.Skip(1))
            {
                try { await s.DisposeAsync().ConfigureAwait(false); }
                catch { /* independent reader handles are best-effort */ }
            }
            await parent.DisposeAsync().ConfigureAwait(false);
        }
    }

    /// <summary>Merges adjacent/overlapping backed ranges and expands each to chunk boundaries.
    /// Returns null when the source declares no ranges (fully backed).</summary>
    private static IReadOnlyList<BackingRange>? MergeAndExpandToChunkGrid(IReadOnlyList<BackingRange>? ranges, long chunkSize, long totalSize)
    {
        if (ranges is null || ranges.Count == 0) return null;
        var sorted = ranges.OrderBy(r => r.Offset).ToList();
        var merged = new List<BackingRange>();
        foreach (var r in sorted)
        {
            if (merged.Count > 0 && r.Offset <= merged[^1].Offset + merged[^1].Length)
            {
                var last = merged[^1];
                merged[^1] = new BackingRange(last.Offset, Math.Max(last.Offset + last.Length, r.Offset + r.Length) - last.Offset);
            }
            else merged.Add(r);
        }
        var grid = new List<BackingRange>();
        foreach (var r in merged)
        {
            long start = r.Offset / chunkSize * chunkSize;
            long end = Math.Min((r.Offset + r.Length + chunkSize - 1) / chunkSize * chunkSize, totalSize);
            if (end > start) grid.Add(new BackingRange(start, end - start));
        }
        return grid;
    }

    private static async Task ReadExactlyAsync(Stream s, long at, Memory<byte> target, CancellationToken ct)
    {
        s.Seek(at, SeekOrigin.Begin);
        int done = 0;
        // a wedged snapshot read must FAIL the backup honestly, never hang the pipeline forever
        using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeoutCts.CancelAfter(TimeSpan.FromMinutes(2));
        try
        {
            while (done < target.Length)
            {
                int n = await s.ReadAsync(target[done..], timeoutCts.Token).ConfigureAwait(false);
                if (n == 0) throw new EndOfStreamException($"source ended at {at + done} of {target.Length + at} requested bytes");
                done += n;
            }
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            throw new IOException($"source read timed out at offset {at + done} ({done}/{target.Length} bytes) — snapshot device stopped responding");
        }
    }

    private static bool IsAllZero(ReadOnlySpan<byte> span)
    {
        foreach (var b in span) if (b != 0) return false;
        return true;
    }
}
