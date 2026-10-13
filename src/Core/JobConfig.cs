using System.Text.Json;
using System.Text.Json.Serialization;
using OpenReflect.Core.Abstractions;

namespace OpenReflect.Core;

/// <summary>Job JSON document (ARCHITECTURE.md §5, schemaVersion 1). Unknown properties are ignored;
/// schemaVersion > 1 is rejected with NotSupported.</summary>
public sealed record JobDocument
{
    [JsonPropertyName("schemaVersion")] public int SchemaVersion { get; init; } = 1;
    [JsonPropertyName("jobId")] public Guid JobId { get; init; } = Guid.NewGuid();
    [JsonPropertyName("name")] public string Name { get; init; } = "";
    [JsonPropertyName("source")] public SourceSpec Source { get; init; } = new();
    [JsonPropertyName("destination")] public DestinationSpec Destination { get; init; } = new();
    [JsonPropertyName("chain")] public ChainSpec Chain { get; init; } = new();
    [JsonPropertyName("options")] public OptionsSpec Options { get; init; } = new();
    [JsonPropertyName("verifyAfterWrite")] public bool VerifyAfterWrite { get; init; } = true;

    public sealed record SourceSpec([property: JsonPropertyName("path")] string Path = "");
    public sealed record DestinationSpec(
        [property: JsonPropertyName("directory")] string Directory = "",
        [property: JsonPropertyName("fileNamePrefix")] string FileNamePrefix = "image");
    public sealed record ChainSpec(
        [property: JsonPropertyName("kind")] string Kind = "full",
        [property: JsonPropertyName("maxDeltaDepth")] int MaxDeltaDepth = 7);
    public sealed record OptionsSpec(
        [property: JsonPropertyName("chunkSizeBytes")] long ChunkSizeBytes = 4 * 1024 * 1024,
        [property: JsonPropertyName("compression")] string Compression = "deflate",
        [property: JsonPropertyName("compressionLevel")] int CompressionLevel = 6,
        [property: JsonPropertyName("encryption")] string Encryption = "none",
        [property: JsonPropertyName("kdfIterations")] int KdfIterations = 600000,
        [property: JsonPropertyName("passwordEnvVar")] string? PasswordEnvVar = null,
        [property: JsonPropertyName("skipZeroRegions")] bool SkipZeroRegions = true);
}

public static class JobConfig
{
    private static readonly long[] ValidChunkSizes =
        { 512L * 1024, 1L << 20, 2L << 20, 4L << 20, 8L << 20, 16L << 20 };

    public static Result<JobDocument> Load(string path)
    {
        if (!File.Exists(path)) return Error.Of(ErrorCode.SourceNotFound, $"job file not found: {path}");
        JsonDocument doc;
        try { doc = JsonDocument.Parse(File.ReadAllText(path)); }
        catch (JsonException ex) { return Error.Of(ErrorCode.InvalidArgument, "job file is not valid JSON", ex.Message); }
        using (doc)
        {
            if (doc.RootElement.ValueKind != JsonValueKind.Object)
                return Error.Of(ErrorCode.InvalidArgument, "job file root must be an object");
            if (doc.RootElement.TryGetProperty("schemaVersion", out var sv) && sv.TryGetInt32(out var version) && version > 1)
                return Error.Of(ErrorCode.NotSupported, $"job schemaVersion {version} is newer than this build");
        }

        JobDocument job;
        try { job = JsonSerializer.Deserialize<JobDocument>(File.ReadAllText(path)) ?? throw new JsonException("null document"); }
        catch (JsonException ex) { return Error.Of(ErrorCode.InvalidArgument, "job file failed schema mapping", ex.Message); }

        if (job.Source.Path.Length == 0) return Error.Of(ErrorCode.InvalidArgument, "source.path is required");
        if (job.Destination.Directory.Length == 0) return Error.Of(ErrorCode.InvalidArgument, "destination.directory is required");
        if (!ValidChunkSizes.Contains(job.Options.ChunkSizeBytes))
            return Error.Of(ErrorCode.InvalidArgument, $"chunkSizeBytes {job.Options.ChunkSizeBytes} not in the valid set (512KiB..16MiB powers of two)");
        if (job.Options.Compression is not ("none" or "deflate"))
            return Error.Of(ErrorCode.InvalidArgument, $"compression '{job.Options.Compression}' not supported (none|deflate; zstd in 0.2)");
        if (job.Options.Encryption is not ("none" or "aes256gcm"))
            return Error.Of(ErrorCode.InvalidArgument, $"encryption '{job.Options.Encryption}' not supported (none|aes256gcm)");
        if (job.Options.Encryption == "aes256gcm" && string.IsNullOrWhiteSpace(job.Options.PasswordEnvVar))
            return Error.Of(ErrorCode.InvalidArgument, "passwordEnvVar is required with aes256gcm (passwords are never stored in job files, D10)");
        if (job.Options.KdfIterations is < 100000 or > 5_000_000)
            return Error.Of(ErrorCode.InvalidArgument, "kdfIterations must be within 100000..5000000");
        if (job.Chain.Kind is not ("full" or "incremental" or "differential"))
            return Error.Of(ErrorCode.InvalidArgument, $"chain.kind '{job.Chain.Kind}' not supported");

        return job;
    }

    /// <summary>Builds engine options from a job; the password comes from the environment (D10).</summary>
    public static Result<ImageOptions> ToImageOptions(this JobDocument job)
    {
        string? PasswordProvider() => job.Options.PasswordEnvVar is null ? null : Environment.GetEnvironmentVariable(job.Options.PasswordEnvVar);
        var opt = new ImageOptions
        {
            ChunkSizeBytes = job.Options.ChunkSizeBytes,
            Compression = job.Options.Compression == "deflate" ? CompressionKind.Deflate : CompressionKind.None,
            CompressionLevel = job.Options.CompressionLevel,
            Encryption = job.Options.Encryption == "aes256gcm" ? EncryptionKind.Aes256Gcm : EncryptionKind.None,
            KdfIterations = job.Options.KdfIterations,
            SkipZeroRegions = job.Options.SkipZeroRegions,
            PasswordProvider = PasswordProvider,
        };
        if (opt.Encryption == EncryptionKind.Aes256Gcm && string.IsNullOrEmpty(PasswordProvider()))
            return Error.Of(ErrorCode.InvalidArgument, $"environment variable '{job.Options.PasswordEnvVar}' is not set");
        return opt;
    }
}
