using OpenReflect.Core.Abstractions;
using OpenReflect.Format;

namespace OpenReflect.Verify;

/// <summary>Three-depth verification (ARCHITECTURE.md): Structure = openable; Manifest = open + index integrity;
/// Full = every stored chunk decrypted, decompressed and re-hashed.</summary>
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
