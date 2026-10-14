using System.Buffers.Binary;
using System.Security.Cryptography;

namespace OpenReflect.Format;

internal static class OrfPrimitives
{
    public const int HeaderSize = 128;
    public const int RecordOverhead = 40 + 32;   // record header (40) + plaintext sha256 (32)
    public const int FooterSize = 64;
    public const uint HeaderMagic = 0x4946524F;  // "ORFI" little-endian read
    public const uint FooterMagic = 0x4646524F;  // "ORFF"
    public const uint FormatVersion = 2;
    /// <summary>Versions the reader accepts: v1 (codec implied Deflate) and v2 (codec byte present).</summary>
    public const uint MinReaderVersion = 1;

    /// <summary>Codec id (0x78, format v2). Values mirror CompressionKind.</summary>
    public const int H_Comp = 0x78;

    public const ushort FlagDirtyShutdown = 0x0001;
    public const ushort FlagEncrypted = 0x0002;
    public const ushort FlagIndexComplete = 0x0004;

    public const ushort RecFlagCompressed = 0x0001;
    public const ushort RecFlagEncrypted = 0x0002;

    // header offsets
    public const int H_ImageId = 0x0C, H_ParentId = 0x1C, H_ChainKind = 0x2C, H_ChunkShift = 0x2D,
        H_Flags = 0x2E, H_CreatedAt = 0x30, H_SourceLabel = 0x38, H_KdfIters = 0x58, H_KdfSalt = 0x5C,
        H_NoncePrefix = 0x6C, H_ChunkCount = 0x70, H_Reserved = 0x78, H_Crc = 0x7C;

    // record offsets
    public const int R_Index = 0, R_Offset = 8, R_Original = 0x10, R_Stored = 0x18, R_Flags = 0x20, R_Hash = 0x28;

    public static uint Crc32(ReadOnlySpan<byte> data)
    {
        uint crc = 0xFFFFFFFFu;
        foreach (byte b in data)
        {
            crc ^= b;
            for (int i = 0; i < 8; i++)
                crc = (crc >> 1) ^ (0xEDB88320u & (uint)(-(int)(crc & 1)));
        }
        return ~crc;
    }

    public static byte[] BuildHeader(OrfCreateSpec spec, Guid imageId, ushort flags, long chunkCount,
        byte[] kdfSalt, byte[] noncePrefix, DateTimeOffset createdAt)
    {
        var h = new byte[HeaderSize];
        BinaryPrimitives.WriteUInt32LittleEndian(h.AsSpan(0x00), HeaderMagic);
        BinaryPrimitives.WriteUInt32LittleEndian(h.AsSpan(0x04), FormatVersion);
        BinaryPrimitives.WriteUInt32LittleEndian(h.AsSpan(0x08), HeaderSize);
        spec.ImageId.ToByteArray().AsSpan().CopyTo(h.AsSpan(H_ImageId));           // placeholder; overwritten below
        imageId.ToByteArray().AsSpan().CopyTo(h.AsSpan(H_ImageId));
        spec.ParentImageId.ToByteArray().AsSpan().CopyTo(h.AsSpan(H_ParentId));
        h[H_ChainKind] = (byte)spec.ChainKind;
        int shift = 0; for (long s = spec.ChunkSizeBytes; s > 1; s >>= 1) shift++;
        h[H_ChunkShift] = (byte)shift;
        BinaryPrimitives.WriteUInt16LittleEndian(h.AsSpan(H_Flags), flags);
        BinaryPrimitives.WriteUInt64LittleEndian(h.AsSpan(H_CreatedAt), (ulong)createdAt.ToUnixTimeSeconds());
        var label = System.Text.Encoding.UTF8.GetBytes(spec.SourceLabel ?? "");
        label.AsSpan(0, Math.Min(32, label.Length)).CopyTo(h.AsSpan(H_SourceLabel));
        BinaryPrimitives.WriteUInt32LittleEndian(h.AsSpan(H_KdfIters), (uint)spec.KdfIterations);
        kdfSalt.AsSpan().CopyTo(h.AsSpan(H_KdfSalt));
        noncePrefix.AsSpan().CopyTo(h.AsSpan(H_NoncePrefix));
        BinaryPrimitives.WriteUInt64LittleEndian(h.AsSpan(H_ChunkCount), (ulong)Math.Max(0, chunkCount));
        h[H_Comp] = (byte)spec.Compression;          // v2 codec byte; 0x79..0x7B stay zero
        BinaryPrimitives.WriteUInt32LittleEndian(h.AsSpan(H_Crc), Crc32(h.AsSpan(0, H_Crc)));
        return h;
    }

    public static byte[] BuildRecordHeader(long chunkIndex, long logicalOffset, long originalLen, long storedLen, ushort flags, ReadOnlySpan<byte> sha256)
    {
        var r = new byte[RecordOverhead];
        BinaryPrimitives.WriteUInt64LittleEndian(r.AsSpan(R_Index), (ulong)chunkIndex);
        BinaryPrimitives.WriteUInt64LittleEndian(r.AsSpan(R_Offset), (ulong)logicalOffset);
        BinaryPrimitives.WriteUInt64LittleEndian(r.AsSpan(R_Original), (ulong)originalLen);
        BinaryPrimitives.WriteUInt64LittleEndian(r.AsSpan(R_Stored), (ulong)storedLen);
        BinaryPrimitives.WriteUInt16LittleEndian(r.AsSpan(R_Flags), flags);
        sha256.CopyTo(r.AsSpan(R_Hash));
        return r;
    }

    public static byte[] BuildFooter(long indexOffset, long indexLength, long manifestOffset, long logicalSize)
    {
        var f = new byte[FooterSize];
        BinaryPrimitives.WriteUInt32LittleEndian(f.AsSpan(0x00), FooterMagic);
        BinaryPrimitives.WriteUInt32LittleEndian(f.AsSpan(0x04), FormatVersion);
        BinaryPrimitives.WriteUInt64LittleEndian(f.AsSpan(0x08), (ulong)indexOffset);
        BinaryPrimitives.WriteUInt64LittleEndian(f.AsSpan(0x10), (ulong)indexLength);
        BinaryPrimitives.WriteUInt64LittleEndian(f.AsSpan(0x18), (ulong)manifestOffset);
        BinaryPrimitives.WriteUInt64LittleEndian(f.AsSpan(0x20), (ulong)logicalSize);
        BinaryPrimitives.WriteUInt32LittleEndian(f.AsSpan(0x28), Crc32(f.AsSpan(0, 0x28)));
        return f;
    }

    public static byte[] Sha256(ReadOnlySpan<byte> data) => SHA256.HashData(data);
}
