using System.IO.Compression;
using System.Security.Cryptography;
using OpenReflect.Core.Abstractions;

namespace OpenReflect.Format;

internal static class Transforms
{
    /// <summary>Codec dispatch: caller passes the image-level codec (header 0x78 on v2; Deflate implied on v1).</summary>
    public static byte[] Compress(CompressionKind kind, ReadOnlySpan<byte> plaintext, int level) => kind switch
    {
        CompressionKind.None => plaintext.ToArray(),
        CompressionKind.Deflate => CompressDeflate(plaintext, level),
        CompressionKind.Zstd => CompressZstd(plaintext, level),
        _ => throw new NotSupportedException($"codec {kind}"),
    };

    public static byte[] Decompress(CompressionKind kind, ReadOnlySpan<byte> stored, long originalLen) => kind switch
    {
        CompressionKind.None => stored.ToArray(),
        CompressionKind.Deflate => DecompressDeflate(stored, originalLen),
        CompressionKind.Zstd => DecompressZstd(stored, originalLen),
        _ => throw new NotSupportedException($"codec {kind}"),
    };

    private static byte[] CompressDeflate(ReadOnlySpan<byte> plaintext, int level)
    {
        using var ms = new MemoryStream();
        var cl = level switch
        {
            <= 3 => CompressionLevel.Fastest,
            <= 6 => CompressionLevel.Optimal,
            _ => CompressionLevel.SmallestSize,
        };
        using (var ds = new DeflateStream(ms, cl, leaveOpen: true))
            ds.Write(plaintext);
        return ms.ToArray();
    }

    private static byte[] DecompressDeflate(ReadOnlySpan<byte> stored, long originalLen)
    {
        using var src = new MemoryStream(stored.ToArray());
        using var ds = new DeflateStream(src, CompressionMode.Decompress);
        var outBuf = new byte[originalLen];
        int read = 0;
        while (read < originalLen)
        {
            int n = ds.Read(outBuf, read, (int)(originalLen - read));
            if (n == 0) throw new InvalidDataException("deflate stream ended early");
            read += n;
        }
        if (ds.Read(new byte[1], 0, 1) != 0) throw new InvalidDataException("deflate stream has trailing data");
        return outBuf;
    }

    private static int ZstdLevel(int level) => Math.Clamp(level * 2, 1, 19);

    private static byte[] CompressZstd(ReadOnlySpan<byte> plaintext, int level)
    {
        using var c = new ZstdSharp.Compressor(ZstdLevel(level));
        return c.Wrap(plaintext).ToArray();
    }

    private static byte[] DecompressZstd(ReadOnlySpan<byte> stored, long originalLen)
    {
        using var d = new ZstdSharp.Decompressor();
        var result = d.Unwrap(stored, (int)originalLen);
        if (result.Length != originalLen)
            throw new InvalidDataException($"zstd unwrapped to {result.Length} bytes, expected {originalLen}");
        return result.ToArray();
    }

    public static byte[] DeriveKey(string password, ReadOnlySpan<byte> salt, int iterations) =>
        Rfc2898DeriveBytes.Pbkdf2(password, salt.ToArray(), iterations, HashAlgorithmName.SHA256, 32);

    public static byte[] EncryptChunk(byte[] key, ReadOnlySpan<byte> noncePrefix, long chunkIndex, ReadOnlySpan<byte> plaintext)
    {
        Span<byte> nonce = stackalloc byte[12];
        WriteNonce(nonce, noncePrefix, chunkIndex);
        using var gcm = new AesGcm(key, 16);
        var cipher = new byte[plaintext.Length];
        var tag = new byte[16];
        gcm.Encrypt(nonce, plaintext, cipher, tag);
        var payload = new byte[plaintext.Length + 16];
        cipher.CopyTo(payload);
        tag.CopyTo(payload.AsSpan(plaintext.Length));
        return payload;
    }

    /// <summary>Decrypts; returns null when the GCM tag check fails (wrong password or corruption).</summary>
    public static byte[]? TryDecryptChunk(byte[] key, ReadOnlySpan<byte> noncePrefix, long chunkIndex, ReadOnlySpan<byte> payload, long originalLen)
    {
        if (payload.Length != originalLen + 16) return null;
        Span<byte> nonce = stackalloc byte[12];
        WriteNonce(nonce, noncePrefix, chunkIndex);
        try
        {
            using var gcm = new AesGcm(key, 16);
            var plain = new byte[originalLen];
            gcm.Decrypt(nonce, payload[..^16], payload[^16..], plain);
            return plain;
        }
        catch (System.Security.Cryptography.AuthenticationTagMismatchException) { return null; }
    }

    private static void WriteNonce(Span<byte> nonce, ReadOnlySpan<byte> prefix, long chunkIndex)
    {
        prefix.CopyTo(nonce);
        System.Buffers.Binary.BinaryPrimitives.WriteUInt64LittleEndian(nonce[4..], (ulong)chunkIndex);
    }
}
