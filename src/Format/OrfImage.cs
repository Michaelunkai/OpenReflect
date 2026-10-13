using OpenReflect.Core.Abstractions;

namespace OpenReflect.Format;

/// <summary>Factory facade over the .orf format.</summary>
public static class OrfImage
{
    public static Task<OrfWriter> CreateAsync(string path, OrfCreateSpec spec, CancellationToken ct = default)
        => OrfWriter.CreateAsync(path, spec, ct);

    public static Task<Result<OrfReader>> OpenAsync(string path, string? password = null, CancellationToken ct = default)
        => OrfReader.OpenAsync(path, password, ct);

    public static Task<Result<OrfWriter>> ResumeAsync(string path, CancellationToken ct = default)
        => OrfWriter.ResumeAsync(path, ct);
}
