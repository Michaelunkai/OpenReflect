using System.IO.Compression;
using System.Security.Cryptography;
using OpenReflect.Core.Abstractions;

namespace OpenReflect.Format;

internal static class Transforms
{
    public static byte[] Compress(ReadOnlySpan<byte> plaintext, int level)
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

    public static byte[] Decompress(ReadOnlySpan<byte> stored, long originalLen)
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
