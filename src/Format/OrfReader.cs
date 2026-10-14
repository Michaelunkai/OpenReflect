using System.Buffers.Binary;
using System.Security.Cryptography;
using OpenReflect.Core.Abstractions;

namespace OpenReflect.Format;

public sealed class OrfReader : IImageReader
{
    private readonly FileStream _fs;
    private readonly Dictionary<long, long> _index;          // chunkIndex -> record file offset
    private readonly byte[] _header;
    private readonly byte[] _footer;
    private readonly byte[]? _key;
    private readonly bool _indexComplete;
    private bool _v1Compat;

    private OrfReader(FileStream fs, byte[] header, byte[]? footer, Dictionary<long, long> index, bool indexComplete, string? password, bool v1Compat)
    {
        _fs = fs; _header = header; _footer = footer; _index = index; _indexComplete = indexComplete;
        Password = password;
        _v1Compat = v1Compat;
        bool encrypted = (ReadFlags() & OrfPrimitives.FlagEncrypted) != 0;
        _key = encrypted ? Transforms.DeriveKey(Password!, HeaderSalt, KdfIterations) : null;
    }

    /// <summary>The password supplied at open time; never persisted or logged.</summary>
    public string? Password { get; }

    private ushort ReadFlags() => BinaryPrimitives.ReadUInt16LittleEndian(_header.AsSpan(OrfPrimitives.H_Flags));
    private long ChunkSize => 1L << _header[OrfPrimitives.H_ChunkShift];
    private int KdfIterations => (int)BinaryPrimitives.ReadUInt32LittleEndian(_header.AsSpan(OrfPrimitives.H_KdfIters));
    /// <summary>Image-level codec. v2 reads it from the header; v1 images are Deflate when the
    /// RecFlagCompressed path was used (the only compressed codec v1 defined), else None.</summary>
    private CompressionKind Codec => _v1Compat ? CompressionKind.Deflate : (CompressionKind)_header[OrfPrimitives.H_Comp];
    private byte[] HeaderSalt => _header.AsSpan(OrfPrimitives.H_KdfSalt, 16).ToArray();
    private byte[] NoncePrefix => _header.AsSpan(OrfPrimitives.H_NoncePrefix, 4).ToArray();
    private long? FooterLogicalSize => _footer is null
        ? null
        : (long)BinaryPrimitives.ReadUInt64LittleEndian(_footer.AsSpan(0x20, 8));

    public ImageHeaderInfo Header => new(
        new Guid(_header.AsSpan(OrfPrimitives.H_ImageId, 16)),
        new Guid(_header.AsSpan(OrfPrimitives.H_ParentId, 16)),
        (ChainKind)_header[OrfPrimitives.H_ChainKind],
        ChunkSize,
        Encrypted: (ReadFlags() & OrfPrimitives.FlagEncrypted) != 0,
        IsComplete: _indexComplete && (ReadFlags() & OrfPrimitives.FlagDirtyShutdown) == 0,
        ChunkCount: _indexComplete
            ? (long)BinaryPrimitives.ReadUInt64LittleEndian(_header.AsSpan(OrfPrimitives.H_ChunkCount, 8))
            : _index.Count,
        LogicalSizeBytes: FooterLogicalSize
            ?? (_indexComplete
                ? (long)BinaryPrimitives.ReadUInt64LittleEndian(_header.AsSpan(OrfPrimitives.H_ChunkCount, 8)) * ChunkSize
                : _index.Count * ChunkSize),
        SourceLabel: System.Text.Encoding.UTF8.GetString(_header.AsSpan(OrfPrimitives.H_SourceLabel, 32)).TrimEnd('\0'),
        CreatedAt: DateTimeOffset.FromUnixTimeSeconds((long)BinaryPrimitives.ReadUInt64LittleEndian(_header.AsSpan(OrfPrimitives.H_CreatedAt, 8))));

    public string FilePath => _fs.Name;

    public bool HasChunk(long chunkIndex) => _index.ContainsKey(chunkIndex);

    public async Task<Result<byte[]>> GetChunkHashAsync(long chunkIndex, CancellationToken ct = default)
    {
        if (!_index.TryGetValue(chunkIndex, out var off))
            return Error.Of(ErrorCode.BadFormat, $"chunk {chunkIndex} not present in this image");
        var buf = new byte[OrfPrimitives.RecordOverhead];
        _fs.Seek(off, SeekOrigin.Begin);
        await _fs.ReadExactlyAsync(buf, ct).ConfigureAwait(false);
        return buf.AsSpan(OrfPrimitives.R_Hash, 32).ToArray();
    }

    public async Task<Result<long>> GetChunkFileOffsetAsync(long chunkIndex, CancellationToken ct = default)
        => _index.TryGetValue(chunkIndex, out var off) ? off : Error.Of(ErrorCode.BadFormat, $"chunk {chunkIndex} not present in this image (inherited from chain)");

    public async Task<Result<byte[]>> ReadChunkAsync(long chunkIndex, CancellationToken ct = default)
    {
        if (!_index.TryGetValue(chunkIndex, out var off))
            return Error.Of(ErrorCode.BadFormat, $"chunk {chunkIndex} not present in this image");
        var buf = new byte[OrfPrimitives.RecordOverhead];
        _fs.Seek(off, SeekOrigin.Begin);
        for (int read = 0; read < buf.Length;)
        {
            int n = await _fs.ReadAsync(buf.AsMemory(read), ct).ConfigureAwait(false);
            if (n == 0) return Error.Of(ErrorCode.CorruptChunk, $"record header for chunk {chunkIndex} truncated");
            read += n;
        }
        long idx = (long)BinaryPrimitives.ReadUInt64LittleEndian(buf.AsSpan(OrfPrimitives.R_Index));
        if (idx != chunkIndex) return Error.Of(ErrorCode.CorruptChunk, $"index mismatch at chunk {chunkIndex}");
        long originalLen = (long)BinaryPrimitives.ReadUInt64LittleEndian(buf.AsSpan(OrfPrimitives.R_Original));
        long storedLen = (long)BinaryPrimitives.ReadUInt64LittleEndian(buf.AsSpan(OrfPrimitives.R_Stored));
        ushort rflags = BinaryPrimitives.ReadUInt16LittleEndian(buf.AsSpan(OrfPrimitives.R_Flags));
        var expectedHash = buf.AsSpan(OrfPrimitives.R_Hash, 32).ToArray();

        var payload = new byte[storedLen];
        for (int read = 0; read < payload.Length;)
        {
            int n = await _fs.ReadAsync(payload.AsMemory(read), ct).ConfigureAwait(false);
            if (n == 0) return Error.Of(ErrorCode.CorruptChunk, $"payload for chunk {chunkIndex} truncated");
            read += n;
        }

        byte[] plain;
        if ((rflags & OrfPrimitives.RecFlagEncrypted) != 0)
        {
            var dec = Transforms.TryDecryptChunk(_key!, NoncePrefix, chunkIndex, payload, originalLen);
            if (dec is null) return Error.Of(ErrorCode.WrongPassword, $"GCM authentication failed for chunk {chunkIndex} (wrong password or corruption)");
            plain = dec;
        }
        else plain = payload;

        if ((rflags & OrfPrimitives.RecFlagCompressed) != 0)
        {
            try { plain = Transforms.Decompress(Codec, plain, originalLen); }
            catch (InvalidDataException ex) { return Error.Of(ErrorCode.CorruptChunk, $"chunk {chunkIndex} decompress failed", ex.Message); }
            catch (NotSupportedException ex) { return Error.Of(ErrorCode.UnsupportedVersion, $"chunk {chunkIndex}: {ex.Message}"); }
        }
        else if (plain.Length != originalLen)
            return Error.Of(ErrorCode.CorruptChunk, $"chunk {chunkIndex} length mismatch");

        if (!OrfPrimitives.Sha256(plain).AsSpan().SequenceEqual(expectedHash))
            return Error.Of(ErrorCode.CorruptChunk, $"SHA-256 mismatch for chunk {chunkIndex}");
        return plain;
    }

    public async ValueTask DisposeAsync() => await _fs.DisposeAsync().ConfigureAwait(false);

    private static async Task<Result<OrfReader>> FailAsync(FileStream fs, Error e)
    {
        await fs.DisposeAsync().ConfigureAwait(false);
        return Result<OrfReader>.Fail(e);
    }

    // ---- factory ----
    public static async Task<Result<OrfReader>> OpenAsync(string path, string? password = null, CancellationToken ct = default)
    {
        if (!File.Exists(path)) return Error.Of(ErrorCode.SourceNotFound, $"image not found: {path}");
        var fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read, 1 << 16, useAsync: true);
        try
        {
            if (fs.Length < OrfPrimitives.HeaderSize) return await FailAsync(fs, Error.Of(ErrorCode.BadFormat, "file smaller than header"));
            var header = new byte[OrfPrimitives.HeaderSize];
            await fs.ReadExactlyAsync(header, ct).ConfigureAwait(false);

            if (BinaryPrimitives.ReadUInt32LittleEndian(header) != OrfPrimitives.HeaderMagic)
                return await FailAsync(fs, Error.Of(ErrorCode.BadFormat, "not an .orf image (bad magic)"));
            uint fileVersion = BinaryPrimitives.ReadUInt32LittleEndian(header.AsSpan(4));
            bool v1Compat = fileVersion == 1;
            if (fileVersion is < OrfPrimitives.MinReaderVersion or > OrfPrimitives.FormatVersion)
                return await FailAsync(fs, Error.Of(ErrorCode.UnsupportedVersion, $"format version {fileVersion} not supported (reader accepts {OrfPrimitives.MinReaderVersion}..{OrfPrimitives.FormatVersion})"));
            if (!v1Compat && header[OrfPrimitives.H_Comp] > (byte)CompressionKind.Zstd)
                return await FailAsync(fs, Error.Of(ErrorCode.UnsupportedVersion, $"unknown codec id {header[OrfPrimitives.H_Comp]} at header 0x78"));
            uint crc = BinaryPrimitives.ReadUInt32LittleEndian(header.AsSpan(OrfPrimitives.H_Crc));
            if (crc != OrfPrimitives.Crc32(header.AsSpan(0, OrfPrimitives.H_Crc)))
                return await FailAsync(fs, Error.Of(ErrorCode.CorruptHeader, "header CRC-32 mismatch"));

            bool encrypted = (BinaryPrimitives.ReadUInt16LittleEndian(header.AsSpan(OrfPrimitives.H_Flags)) & OrfPrimitives.FlagEncrypted) != 0;
            if (encrypted && password is null) return await FailAsync(fs, Error.Of(ErrorCode.WrongPassword, "image is encrypted; password required"));

            byte[]? footer = null;
            var index = new Dictionary<long, long>();
            bool indexComplete = false;

            if (fs.Length >= OrfPrimitives.HeaderSize + OrfPrimitives.FooterSize)
            {
                fs.Seek(-OrfPrimitives.FooterSize, SeekOrigin.End);
                footer = new byte[OrfPrimitives.FooterSize];
                await fs.ReadExactlyAsync(footer, ct).ConfigureAwait(false);
                    if (BinaryPrimitives.ReadUInt32LittleEndian(footer) == OrfPrimitives.FooterMagic &&
                    BinaryPrimitives.ReadUInt32LittleEndian(footer.AsSpan(0x28)) == OrfPrimitives.Crc32(footer.AsSpan(0, 0x28)) &&
                    BinaryPrimitives.ReadUInt32LittleEndian(footer.AsSpan(4)) is >= OrfPrimitives.MinReaderVersion and <= OrfPrimitives.FormatVersion)
                {
                    long indexOffset = (long)BinaryPrimitives.ReadUInt64LittleEndian(footer.AsSpan(0x08));
                    long indexLength = (long)BinaryPrimitives.ReadUInt64LittleEndian(footer.AsSpan(0x10));
                    long manifestOffset = (long)BinaryPrimitives.ReadUInt64LittleEndian(footer.AsSpan(0x18));

                    var idx = new byte[indexLength];
                    fs.Seek(indexOffset, SeekOrigin.Begin);
                    await fs.ReadExactlyAsync(idx, ct).ConfigureAwait(false);
                    var manifest = new byte[32];
                    fs.Seek(manifestOffset, SeekOrigin.Begin);
                    await fs.ReadExactlyAsync(manifest, ct).ConfigureAwait(false);

                    if (!OrfPrimitives.Sha256(idx).AsSpan().SequenceEqual(manifest))
                        return await FailAsync(fs, Error.Of(ErrorCode.CorruptChunk, "manifest mismatch (index corrupted)"));

                    long count = (long)BinaryPrimitives.ReadUInt64LittleEndian(idx);
                    for (long i = 0; i < count; i++)
                    {
                        long ci = (long)BinaryPrimitives.ReadUInt64LittleEndian(idx.AsSpan((int)(8 + i * 16)));
                        long off = (long)BinaryPrimitives.ReadUInt64LittleEndian(idx.AsSpan((int)(8 + i * 16 + 8)));
                        index[ci] = off;
                    }
                    indexComplete = true;
                }
            }

            if (!indexComplete)
            {
                // salvage scan: records from header to EOF
                fs.Seek(OrfPrimitives.HeaderSize, SeekOrigin.Begin);
                long pos = OrfPrimitives.HeaderSize;
                while (pos + OrfPrimitives.RecordOverhead <= fs.Length)
                {
                    var rec = new byte[OrfPrimitives.RecordOverhead];
                    await fs.ReadExactlyAsync(rec, ct).ConfigureAwait(false);
                    long ci = (long)BinaryPrimitives.ReadUInt64LittleEndian(rec.AsSpan(OrfPrimitives.R_Index));
                    long stored = (long)BinaryPrimitives.ReadUInt64LittleEndian(rec.AsSpan(OrfPrimitives.R_Stored));
                    if (ci < 0 || stored < 0 || stored > fs.Length) break;   // garbage at EOF
                    index[ci] = pos;
                    pos += OrfPrimitives.RecordOverhead + stored;
                    fs.Seek(pos, SeekOrigin.Begin);
                }
            }

            return new OrfReader(fs, header, footer, index, indexComplete, password, v1Compat);
        }
        catch
        {
            await fs.DisposeAsync().ConfigureAwait(false);
            throw;
        }
    }
}
