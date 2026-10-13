using System.Security.Cryptography;
using OpenReflect.Core.Abstractions;

namespace OpenReflect.Format;

/// <summary>Parameters for a new .orf file.</summary>
public sealed record OrfCreateSpec
{
    public required Guid ImageId { get; init; }
    public Guid ParentImageId { get; init; }
    public required ChainKind ChainKind { get; init; }
    public required long ChunkSizeBytes { get; init; }
    public required string SourceLabel { get; init; }
    public CompressionKind Compression { get; init; } = CompressionKind.None;
    public int CompressionLevel { get; init; }
    public EncryptionKind Encryption { get; init; } = EncryptionKind.None;
    public int KdfIterations { get; init; } = 600000;
    /// <summary>Required when Encryption != None. Consumed immediately; never retained.</summary>
    public string? Password { get; init; }
    /// <summary>Source size in bytes; header logicalChunkCount derives from it. Fallback: stored-grid estimate.</summary>
    public long? LogicalSizeBytes { get; init; }
}

public sealed class OrfWriter : IImageWriter
{
    private readonly FileStream _fs;
    private readonly byte[] _headerBytes;
    private byte[]? _key;
    private readonly OrfCreateSpec _spec;
    private readonly List<(long index, long offset)> _records = new();
    private bool _committed, _abandoned;
    internal string? _resumePassword;

    private OrfWriter(FileStream fs, byte[] headerBytes, OrfCreateSpec spec, byte[]? key)
    {
        _fs = fs; _headerBytes = headerBytes; _spec = spec; _key = key;
    }

    public static async Task<OrfWriter> CreateAsync(string path, OrfCreateSpec spec, CancellationToken ct = default)
    {
        if (spec.Encryption == EncryptionKind.Aes256Gcm && string.IsNullOrEmpty(spec.Password))
            throw new ArgumentException("Password is required when encryption is enabled.", nameof(spec));
        if (spec.Compression == CompressionKind.Zstd)
            throw new NotSupportedException("zstd codec arrives in 0.2; use None or Deflate.");

        var imageId = spec.ImageId == default ? Guid.NewGuid() : spec.ImageId;
        var salt = spec.Encryption == EncryptionKind.Aes256Gcm ? RandomNumberGenerator.GetBytes(16) : new byte[16];
        var nonce = RandomNumberGenerator.GetBytes(4);
        ushort flags = OrfPrimitives.FlagDirtyShutdown;                       // D5: dirty until CommitAsync
        if (spec.Encryption == EncryptionKind.Aes256Gcm) flags |= OrfPrimitives.FlagEncrypted;
        var header = OrfPrimitives.BuildHeader(spec, imageId, flags, chunkCount: 0, salt, nonce, DateTimeOffset.UtcNow);

        var fs = new FileStream(path, FileMode.Create, FileAccess.Write, FileShare.Read, 1 << 16, useAsync: true);
        await fs.WriteAsync(header, ct).ConfigureAwait(false);
        byte[]? key = spec.Encryption == EncryptionKind.Aes256Gcm
            ? Transforms.DeriveKey(spec.Password!, salt, spec.KdfIterations) : null;
        var writer = new OrfWriter(fs, header, spec with { ImageId = imageId, ParentImageId = spec.ParentImageId }, key);
        writer._resumePassword = spec.Password;
        return writer;
    }


    /// <summary>Reopens an abandoned (dirty) image and resumes appending. Reuses the stored
    /// imageId/salt/nonce so chunk nonces remain stable; truncates any partial tail record.</summary>
    public static async Task<Result<OrfWriter>> ResumeAsync(string path, CancellationToken ct = default)
    {
        if (!File.Exists(path)) return Error.Of(ErrorCode.SourceNotFound, $"no image to resume: {path}");
        var opened = await OrfReader.OpenAsync(path).ConfigureAwait(false);
        if (!opened.IsSuccess) return Result<OrfWriter>.Fail(opened.Error);
        await using (var probe = opened.Value)
        {
            if (probe.Header.IsComplete) return Error.Of(ErrorCode.InvalidArgument, "image is already complete; nothing to resume");
        }

        var fs = new FileStream(path, FileMode.Open, FileAccess.ReadWrite, FileShare.Read, 1 << 16, useAsync: true);
        byte[] header = new byte[OrfPrimitives.HeaderSize];
        fs.Seek(0, SeekOrigin.Begin);
        await fs.ReadExactlyAsync(header, ct).ConfigureAwait(false);
        long pos = OrfPrimitives.HeaderSize;
        var records = new List<(long index, long offset)>();
        while (pos + OrfPrimitives.RecordOverhead <= fs.Length)
        {
            var rec = new byte[OrfPrimitives.RecordOverhead];
            fs.Seek(pos, SeekOrigin.Begin);
            if (await fs.ReadAsync(rec, ct) != OrfPrimitives.RecordOverhead) break;
            long logical = (long)System.Buffers.Binary.BinaryPrimitives.ReadUInt64LittleEndian(rec.AsSpan(OrfPrimitives.R_Index));
            long stored = (long)System.Buffers.Binary.BinaryPrimitives.ReadUInt64LittleEndian(rec.AsSpan(OrfPrimitives.R_Stored));
            if (logical < 0 || stored < 0 || pos + OrfPrimitives.RecordOverhead + stored > fs.Length) break;
            records.Add((logical, pos));
            pos += OrfPrimitives.RecordOverhead + stored;
        }
        fs.SetLength(pos);
        fs.Seek(pos, SeekOrigin.Begin);

        var spec = SpecFromHeader(header);
        var w = new OrfWriter(fs, header, spec, null);
        if (spec.Encryption == EncryptionKind.Aes256Gcm)
            return Error.Of(ErrorCode.NotSupported, "resume of encrypted images arrives in 0.2");
        w.RestoreState(records);
        return w;
    }

    private static OrfCreateSpec SpecFromHeader(byte[] header)
    {
        ushort flags = System.Buffers.Binary.BinaryPrimitives.ReadUInt16LittleEndian(header.AsSpan(OrfPrimitives.H_Flags));
        long chunkSize = 1L << header[OrfPrimitives.H_ChunkShift];
        return new OrfCreateSpec
        {
            ImageId = new Guid(header.AsSpan(OrfPrimitives.H_ImageId, 16)),
            ParentImageId = new Guid(header.AsSpan(OrfPrimitives.H_ParentId, 16)),
            ChainKind = (ChainKind)header[OrfPrimitives.H_ChainKind],
            ChunkSizeBytes = chunkSize,
            SourceLabel = System.Text.Encoding.UTF8.GetString(header.AsSpan(OrfPrimitives.H_SourceLabel, 32)).TrimEnd('\0'),
            Encryption = (flags & OrfPrimitives.FlagEncrypted) != 0 ? EncryptionKind.Aes256Gcm : EncryptionKind.None,
            KdfIterations = (int)System.Buffers.Binary.BinaryPrimitives.ReadUInt32LittleEndian(header.AsSpan(OrfPrimitives.H_KdfIters)),
            LogicalSizeBytes = (long)System.Buffers.Binary.BinaryPrimitives.ReadUInt64LittleEndian(header.AsSpan(OrfPrimitives.H_ChunkCount, 8)) * chunkSize,
        };
    }

    private void RestoreState(List<(long index, long offset)> records)
    {
        _records.Clear();
        _records.AddRange(records);
    }

    public async Task<Result> AppendChunkAsync(long chunkIndex, ReadOnlyMemory<byte> plaintext, CancellationToken ct = default)
    {
        ObjectDisposedException.ThrowIf(_abandoned, this);
        if (_committed) return Error.Of(ErrorCode.Internal, "writer already committed");
        if (_records.Count > 0 && _records[^1].index >= chunkIndex)
            return Error.Of(ErrorCode.InvalidArgument, $"chunkIndex {chunkIndex} out of order; must exceed {_records[^1].index} (logical indices may gap for omitted sparse regions)");

        ushort rflags = 0;
        var stored = plaintext.ToArray();

        if (_spec.Compression == CompressionKind.Deflate && plaintext.Length > 0)
        {
            var compressed = Transforms.Compress(stored, _spec.CompressionLevel);
            if (compressed.Length < stored.Length) { stored = compressed; rflags |= OrfPrimitives.RecFlagCompressed; }
        }
        if (_spec.Encryption == EncryptionKind.Aes256Gcm)
        {
            stored = Transforms.EncryptChunk(_key!, _headerBytes.AsSpan(OrfPrimitives.H_NoncePrefix, 4), chunkIndex, stored);
            rflags |= OrfPrimitives.RecFlagEncrypted;
        }

        var hash = OrfPrimitives.Sha256(plaintext.Span);
        var rec = OrfPrimitives.BuildRecordHeader(chunkIndex, chunkIndex * _spec.ChunkSizeBytes, plaintext.Length, stored.Length, rflags, hash);

        long offset = _fs.Length;
        await _fs.WriteAsync(rec, ct).ConfigureAwait(false);
        await _fs.WriteAsync(stored, ct).ConfigureAwait(false);
        _records.Add((chunkIndex, offset));
        return Result.Ok();
    }

    public async Task<Result<ImageHeaderInfo>> CommitAsync(CancellationToken ct = default)
    {
        ObjectDisposedException.ThrowIf(_abandoned, this);
        if (_committed) return Error.Of(ErrorCode.Internal, "writer already committed");

        // 1) chunk index
        long indexOffset = _fs.Length;
        var idx = new byte[8 + _records.Count * 16];
        System.Buffers.Binary.BinaryPrimitives.WriteUInt64LittleEndian(idx, (ulong)_records.Count);
        for (int i = 0; i < _records.Count; i++)
        {
            System.Buffers.Binary.BinaryPrimitives.WriteUInt64LittleEndian(idx.AsSpan(8 + i * 16), (ulong)_records[i].index);
            System.Buffers.Binary.BinaryPrimitives.WriteUInt64LittleEndian(idx.AsSpan(8 + i * 16 + 8), (ulong)_records[i].offset);
        }
        await _fs.WriteAsync(idx, ct).ConfigureAwait(false);

        // 2) manifest
        long manifestOffset = _fs.Length;
        var manifest = OrfPrimitives.Sha256(idx);
        await _fs.WriteAsync(manifest, ct).ConfigureAwait(false);

        // 3) footer
        long logicalCount = _spec.LogicalSizeBytes is long sz
            ? (sz + _spec.ChunkSizeBytes - 1) / _spec.ChunkSizeBytes
            : _records.Count;
        long exactSize = _spec.LogicalSizeBytes ?? (logicalCount * _spec.ChunkSizeBytes);
        await _fs.WriteAsync(OrfPrimitives.BuildFooter(indexOffset, idx.Length, manifestOffset, exactSize), ct).ConfigureAwait(false);

        // 4) clear dirty flag + write authoritative chunk count, fsync
        ushort flags = OrfPrimitives.FlagIndexComplete;
        if (_spec.Encryption == EncryptionKind.Aes256Gcm) flags |= OrfPrimitives.FlagEncrypted;
        var header = OrfPrimitives.BuildHeader(_spec, _spec.ImageId, flags, logicalCount,
            _headerBytes.AsSpan(OrfPrimitives.H_KdfSalt, 16).ToArray(),
            _headerBytes.AsSpan(OrfPrimitives.H_NoncePrefix, 4).ToArray(),
            DateTimeOffset.FromUnixTimeSeconds((long)System.Buffers.Binary.BinaryPrimitives.ReadUInt64LittleEndian(_headerBytes.AsSpan(OrfPrimitives.H_CreatedAt, 8))));
        _fs.Seek(0, SeekOrigin.Begin);
        await _fs.WriteAsync(header, ct).ConfigureAwait(false);
        await _fs.FlushAsync(ct).ConfigureAwait(false);
        _fs.Seek(0, SeekOrigin.End);
        _committed = true;
        await _fs.DisposeAsync().ConfigureAwait(false);

        return new ImageHeaderInfo(_spec.ImageId, _spec.ParentImageId, _spec.ChainKind, _spec.ChunkSizeBytes,
            Encrypted: _spec.Encryption == EncryptionKind.Aes256Gcm, IsComplete: true,
            ChunkCount: logicalCount, LogicalSizeBytes: exactSize,
            _spec.SourceLabel, DateTimeOffset.UtcNow);
    }

    public async Task AbandonAsync()
    {
        if (_abandoned || _committed) return;
        _abandoned = true;
        await _fs.FlushAsync().ConfigureAwait(false);   // dirty flag stays set; no footer (D5)
        await _fs.DisposeAsync().ConfigureAwait(false);
    }

    public async ValueTask DisposeAsync()
    {
        if (!_committed && !_abandoned) await AbandonAsync().ConfigureAwait(false);
        else await _fs.DisposeAsync().ConfigureAwait(false);
    }
}
