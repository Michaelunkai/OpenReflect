using OpenReflect.Core.Abstractions;
using OpenReflect.Format;

namespace OpenReflect.Verify;

/// <summary>Three-depth verification (ARCHITECTURE.md): Structure = openable; Manifest = open + index integrity;
/// Full = every stored chunk decrypted, decompressed and re-hashed. 0.2: full depth runs on parallel
/// reader handles (one OrfReader per worker; readers are single-handle, never shared across workers).</summary>
public sealed class Verifier : IVerifier
{
    public async Task<VerifyResult> VerifyAsync(string imageFile, VerifyDepth depth = VerifyDepth.Full, IProgress<ProgressInfo>? progress = null, Func<string?>? passwordProvider = null, CancellationToken ct = default)
    {
        var opened = await OrfImage.OpenAsync(imageFile, passwordProvider?.Invoke()).ConfigureAwait(false);
        if (!opened.IsSuccess)
            return new VerifyResult(false, imageFile, IsComplete: false, 0, Array.Empty<ChunkError>(), opened.Error);

        await using var r = opened.Value;
        if (depth == VerifyDepth.Structure)
            return new VerifyResult(true, imageFile, r.Header.IsComplete, 0, Array.Empty<ChunkError>());

        // Manifest depth: the open already validated footer + index + manifest (FORMAT.md §5/§6);
        // additionally walk every stored record header.
        if (depth == VerifyDepth.Manifest)
        {
            var manifestErrors = new List<ChunkError>();
            for (long logical = 0; logical < r.Header.ChunkCount; logical++)
            {
                ct.ThrowIfCancellationRequested();
                if (!r.HasChunk(logical)) continue;
                var hash = await r.GetChunkHashAsync(logical, ct).ConfigureAwait(false);
                if (!hash.IsSuccess) manifestErrors.Add(new ChunkError(logical, hash.Error.Message));
            }
            return new VerifyResult(manifestErrors.Count == 0, imageFile, r.Header.IsComplete, r.Header.ChunkCount, manifestErrors);
        }

        // Full depth: read every stored chunk — ReadChunkAsync verifies GCM tag, decompression and SHA-256.
        // Parallel: N independent reader handles; each worker owns exactly one (format readers are
        // single-file-handle and are never shared across threads).
        int workers = Math.Clamp(Environment.ProcessorCount, 1, 16);
        if (workers == 1 || r.Header.LogicalSizeBytes < 8 << 20)
            return await VerifyFullSequentialAsync(r, imageFile, progress, ct).ConfigureAwait(false);

        var readers = new List<OrfReader> { r };
        try
        {
            for (int i = 1; i < workers; i++)
            {
                var extra = await OrfImage.OpenAsync(imageFile, passwordProvider?.Invoke()).ConfigureAwait(false);
                if (!extra.IsSuccess) break;
                readers.Add(extra.Value);
            }

            long logicalCount = r.Header.ChunkCount;
            long checkedChunks = 0;
            var failures = new System.Collections.Concurrent.ConcurrentBag<ChunkError>();
            var lastReport = DateTime.UtcNow;
            var gate = new object();

            var tasks = readers.Select((reader, workerIdx) => Task.Run(async () =>
            {
                for (long logical = workerIdx; logical < logicalCount; logical += readers.Count)
                {
                    ct.ThrowIfCancellationRequested();
                    if (!reader.HasChunk(logical)) continue;
                    var chunk = await reader.ReadChunkAsync(logical, ct).ConfigureAwait(false);
                    if (!chunk.IsSuccess)
                        failures.Add(new ChunkError(logical, chunk.Error.Message));
                    lock (gate)
                    {
                        checkedChunks++;
                        if (progress is not null && (DateTime.UtcNow - lastReport).TotalMilliseconds > 250)
                        {
                            lastReport = DateTime.UtcNow;
                            progress.Report(new ProgressInfo("verifying", checkedChunks, logicalCount, (int)checkedChunks, (int)logicalCount, 0));
                        }
                    }
                }
            }, ct)).ToArray();
            await Task.WhenAll(tasks).ConfigureAwait(false);

            var list = failures.OrderBy(f => f.ChunkIndex).ToList();
            return new VerifyResult(list.Count == 0, imageFile, r.Header.IsComplete, checkedChunks, list);
        }
        finally
        {
            for (int i = 1; i < readers.Count; i++) await readers[i].DisposeAsync().ConfigureAwait(false);
        }
    }

    private static async Task<VerifyResult> VerifyFullSequentialAsync(OrfReader r, string imageFile, IProgress<ProgressInfo>? progress, CancellationToken ct)
    {
        var failures = new List<ChunkError>();
        long checkedChunks = 0;
        var lastReport = DateTime.UtcNow;
        for (long logical = 0; logical < r.Header.ChunkCount; logical++)
        {
            ct.ThrowIfCancellationRequested();
            if (!r.HasChunk(logical)) continue;
            var chunk = await r.ReadChunkAsync(logical, ct).ConfigureAwait(false);
            if (!chunk.IsSuccess)
                failures.Add(new ChunkError(logical, chunk.Error.Message));
            checkedChunks++;
            if (progress is not null && (DateTime.UtcNow - lastReport).TotalMilliseconds > 250)
            {
                lastReport = DateTime.UtcNow;
                progress.Report(new ProgressInfo("verifying", checkedChunks, r.Header.ChunkCount, (int)checkedChunks, (int)r.Header.ChunkCount, 0));
            }
        }
        return new VerifyResult(failures.Count == 0, imageFile, r.Header.IsComplete, checkedChunks, failures);
    }
}
