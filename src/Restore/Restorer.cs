using OpenReflect.Core.Abstractions;
using OpenReflect.Format;

namespace OpenReflect.Restore;

/// <summary>Chain-merge restore planning and gated execution (ARCHITECTURE.md D12).
///
/// INCIDENT LESSON (2026-10-13): an early version allowed raw-device targets behind flags whose
/// gates were not yet complete — a test reached \\.\PhysicalDrive0 and damaged the primary GPT
/// (repaired from the backup GPT). v0.1 therefore REFUSES every physical-device target outright:
/// restore targets are image files and mounted VHDs only. Real-disk restore ships with the 0.2
/// offline (WinPE) workflow where gates can be verified against a non-running system.</summary>
public sealed class Restorer : IRestorer
{
    /// <summary>Sentinel source for plan regions absent from every chain image (used-sector sparse
    /// zeros): the executor writes zeros for these regions.</summary>
    public const string ZeroFillSource = "";

    public async Task<Result<RestorePlan>> PlanAsync(string imageFile, string targetPath, Func<string?>? passwordProvider = null, CancellationToken ct = default)
    {
        var chain = new List<IImageReader>();
        try
        {
            var opened = await OrfImage.OpenAsync(imageFile, passwordProvider?.Invoke()).ConfigureAwait(false);
            if (!opened.IsSuccess) return Result<RestorePlan>.Fail(opened.Error);
            var current = opened.Value;
            chain.Add(current);
            var seen = new HashSet<Guid> { current.Header.ImageId };
            while (current.Header.ParentImageId != Guid.Empty)
            {
                var parentPath = FindSibling(current.FilePath, current.Header.ParentImageId);
                if (parentPath is null)
                    return Error.Of(ErrorCode.ChainBroken, $"parent image {current.Header.ParentImageId} not found next to {current.FilePath}");
                var parentOpened = await OrfImage.OpenAsync(parentPath, passwordProvider?.Invoke()).ConfigureAwait(false);
                if (!parentOpened.IsSuccess) return Result<RestorePlan>.Fail(parentOpened.Error);
                current = parentOpened.Value;
                if (!seen.Add(current.Header.ImageId))
                    return Error.Of(ErrorCode.ChainBroken, "cycle detected in image chain");
                chain.Add(current);
            }
            foreach (var img in chain)
                if (!img.Header.IsComplete)
                    return Error.Of(ErrorCode.ChainIncomplete, $"chain contains an incomplete image: {img.FilePath}");

            var newest = chain[0];
            long chunkSize = newest.Header.ChunkSizeBytes;
            long logicalSize = newest.Header.LogicalSizeBytes;
            long logicalChunks = (logicalSize + chunkSize - 1) / chunkSize;

            var regions = new List<RestoreRegion>();
            for (long logical = 0; logical < logicalChunks; logical++)
            {
                ct.ThrowIfCancellationRequested();
                var owner = chain.FirstOrDefault(img => img.HasChunk(logical));   // newest-first chain merge
                long len = Math.Min(chunkSize, logicalSize - logical * chunkSize);
                regions.Add(new RestoreRegion(logical * chunkSize, len, owner?.FilePath ?? ZeroFillSource, logical));
            }

            return new RestorePlan(targetPath, logicalSize, regions, chain.Select(c => c.FilePath).ToList());
        }
        finally
        {
            foreach (var img in chain) await img.DisposeAsync().ConfigureAwait(false);
        }
    }

    private static string? FindSibling(string newestPath, Guid parentId)
    {
        var dir = Path.GetDirectoryName(Path.GetFullPath(newestPath)) ?? ".";
        foreach (var candidate in Directory.EnumerateFiles(dir, "*.orf"))
        {
            if (string.Equals(candidate, newestPath, StringComparison.OrdinalIgnoreCase)) continue;
            var bytes = new byte[16];
            try
            {
                using var fs = new FileStream(candidate, FileMode.Open, FileAccess.Read, FileShare.Read);
                if (fs.Length < 128) continue;
                fs.Seek(0x0C, SeekOrigin.Begin);
                if (fs.Read(bytes, 0, 16) != 16) continue;
            }
            catch (IOException) { continue; }
            if (new Guid(bytes) == parentId) return candidate;
        }
        return null;
    }

    public async Task<Result> ExecuteAsync(RestorePlan plan, RestoreOptions options, IProgress<ProgressInfo>? progress = null, CancellationToken ct = default)
    {
        // ---- safety gates (D12), cheap refusals first — nothing has been touched yet ----
        if (plan.TargetPath.StartsWith(@"\\.\", StringComparison.OrdinalIgnoreCase) ||
            plan.TargetPath.StartsWith(@"\\?\PhysicalDrive", StringComparison.OrdinalIgnoreCase))
            return Error.Of(ErrorCode.RestoreSafetyGateBlocked,
                "physical-device restore is disabled in v0.1 (offline/WinPE workflow arrives in 0.2); restore to an image file or mounted VHD");
        if (options.DryRun)
            return Result.Ok();                       // plan-only; nothing is ever written in dry-run
        if (!options.Confirmed)
            return Error.Of(ErrorCode.RestoreSafetyGateBlocked, "restore refused: Confirmed flag not set (D12)");

        // ---- open target ----
        bool createdNew = false;
        FileStream target;
        try
        {
            if (File.Exists(plan.TargetPath))
            {
                var info = new FileInfo(plan.TargetPath);
                if (info.Length < plan.TargetSizeBytes)
                    return Error.Of(ErrorCode.RestoreTargetTooSmall, $"target is {info.Length} bytes but plan needs {plan.TargetSizeBytes}");
                target = new FileStream(plan.TargetPath, FileMode.Open, FileAccess.Write, FileShare.None, 1 << 16, useAsync: true);
            }
            else
            {
                target = new FileStream(plan.TargetPath, FileMode.CreateNew, FileAccess.Write, FileShare.None, 1 << 16, useAsync: true);
                createdNew = true;
            }
        }
        catch (IOException ex) { return Error.Of(ErrorCode.DestinationInUse, $"target cannot be opened for exclusive write: {plan.TargetPath}", ex.Message); }
        catch (UnauthorizedAccessException ex) { return Error.Of(ErrorCode.DestinationAccessDenied, $"access denied writing {plan.TargetPath}", ex.Message); }

        // ---- execute: any failure after this point rolls back a newly-created target ----
        Result outcome = Error.Of(ErrorCode.Internal, "restore did not complete");
        var readers = new Dictionary<string, IImageReader>();
        long done = 0;
        int doneRegions = 0;
        var lastReport = DateTime.UtcNow;
        try
        {
            foreach (var region in plan.Regions)
            {
                ct.ThrowIfCancellationRequested();
                if (region.FromImageFile == ZeroFillSource)
                {
                    target.Seek(region.Offset, SeekOrigin.Begin);
                    await target.WriteAsync(new byte[region.Length].AsMemory(), ct).ConfigureAwait(false);
                    done += region.Length; doneRegions++;
                    continue;
                }
                if (!readers.TryGetValue(region.FromImageFile, out var reader))
                {
                    var opened = await OrfImage.OpenAsync(region.FromImageFile, options.PasswordProvider?.Invoke()).ConfigureAwait(false);
                    if (!opened.IsSuccess) { outcome = Result.Fail(opened.Error); return outcome; }
                    reader = opened.Value;
                    readers[region.FromImageFile] = reader;
                }
                var chunk = await reader.ReadChunkAsync(region.ChunkIndex, ct).ConfigureAwait(false);
                if (!chunk.IsSuccess) { outcome = Result.Fail(chunk.Error); return outcome; }   // wrong password fails before ANY write
                if (chunk.Value.Length != region.Length)
                { outcome = Error.Of(ErrorCode.RegionMismatch, $"region {region.ChunkIndex} length mismatch"); return outcome; }
                target.Seek(region.Offset, SeekOrigin.Begin);
                await target.WriteAsync(chunk.Value.AsMemory(), ct).ConfigureAwait(false);
                done += region.Length; doneRegions++;
                if (progress is not null && (DateTime.UtcNow - lastReport).TotalMilliseconds > 250)
                {
                    lastReport = DateTime.UtcNow;
                    progress.Report(new ProgressInfo("restoring", done, plan.TargetSizeBytes, doneRegions, plan.Regions.Count, 0));
                }
            }
            await target.FlushAsync(ct).ConfigureAwait(false);
            outcome = Result.Ok();
            return outcome;
        }
        catch (OperationCanceledException)
        {
            outcome = Error.Of(ErrorCode.Cancelled, "restore cancelled");
            return outcome;
        }
        catch (IOException ex)
        {
            outcome = Error.Of(ErrorCode.DestinationFull, "write failed during restore", ex.Message);
            return outcome;
        }
        finally
        {
            foreach (var r in readers.Values) await r.DisposeAsync().ConfigureAwait(false);
            await target.DisposeAsync().ConfigureAwait(false);
            if (!outcome.IsSuccess && createdNew)
            { try { File.Delete(plan.TargetPath); } catch (IOException) { /* caller informed of failure */ } }
        }
    }
}
