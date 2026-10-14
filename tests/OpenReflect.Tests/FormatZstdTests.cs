using OpenReflect.Core.Abstractions;
using OpenReflect.Format;
using Xunit;

namespace OpenReflect.Tests;

/// <summary>Format v2: zstd codec end-to-end (create → read → hash equality), deflate regression,
/// and the prepared-chunk API the parallel engine uses (FORMAT.md §2 codec byte at 0x78).</summary>
public class FormatZstdTests
{
    private static string TempDir()
    {
        var d = Path.Combine(Path.GetTempPath(), "orfz-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(d);
        return d;
    }

    private static OrfCreateSpec Spec(CompressionKind kind, int level = 3) => new()
    {
        ImageId = Guid.NewGuid(),
        ChainKind = ChainKind.Full,
        ChunkSizeBytes = 1 << 20,
        SourceLabel = "zstd-test",
        Compression = kind,
        CompressionLevel = level,
    };

    private static byte[] Payload(int len, int seed)
    {
        // compressible pattern + pseudo-random mix, deterministic
        var b = new byte[len];
        var rng = new Random(seed);
        for (int i = 0; i < len; i++) b[i] = (byte)((i % 4096 < 2048) ? (i % 7) : rng.Next(256));
        return b;
    }

    private static void AssertSuccess(Result r)
    {
        if (!r.IsSuccess) throw new Exception($"expected success but got: {r.Error}");
    }

    [Theory]
    [InlineData(CompressionKind.Zstd)]
    [InlineData(CompressionKind.Deflate)]
    [InlineData(CompressionKind.None)]
    public async Task Z1_Codec_Roundtrip_HashEquality(CompressionKind kind)
    {
        var dir = TempDir();
        try
        {
            var file = Path.Combine(dir, $"{kind}.orf");
            var payload = Payload(3 << 20, 42); // > 1 chunk + partial tail
            await using (var w = await OrfImage.CreateAsync(file, Spec(kind)))
            {
                AssertSuccess(await w.AppendChunkAsync(0, payload.AsMemory(0, 1 << 20)));
                AssertSuccess(await w.AppendChunkAsync(1, payload.AsMemory(1 << 20, 1 << 20)));
                AssertSuccess(await w.AppendChunkAsync(2, payload.AsMemory(2 << 20, payload.Length - 2 << 20 > 0 ? (1 << 20) : 0)));
                AssertSuccess(await w.CommitAsync());
            }
            var opened = await OrfImage.OpenAsync(file);
            AssertSuccess(opened);
            await using var r = opened.Value;
            Assert.Equal(3L * (1 << 20), r.Header.LogicalSizeBytes);
            for (long c = 0; c < 2; c++)
            {
                var chunk = await r.ReadChunkAsync(c);
                AssertSuccess(chunk);
                Assert.Equal(payload.AsSpan((int)c * (1 << 20), 1 << 20).ToArray(), chunk.Value);
            }
        }
        finally { try { Directory.Delete(dir, true); } catch (IOException) { } }
    }

    [Fact]
    public async Task Z2_Zstd_Compression_ActuallyStores()
    {
        var dir = TempDir();
        try
        {
            var file = Path.Combine(dir, "z.orf");
            var payload = new byte[1 << 20];
            Array.Fill(payload, (byte)7); // trivially compressible
            await using (var w = await OrfImage.CreateAsync(file, Spec(CompressionKind.Zstd)))
            {
                AssertSuccess(await w.AppendChunkAsync(0, payload));
                AssertSuccess(await w.CommitAsync());
            }
            Assert.True(new FileInfo(file).Length < (1 << 20), $"expected stored size below plaintext, got {new FileInfo(file).Length}");
            var opened = await OrfImage.OpenAsync(file);
            await using var r = opened.Value;
            var chunk = await r.ReadChunkAsync(0);
            AssertSuccess(chunk);
            Assert.Equal(payload, chunk.Value);
        }
        finally { try { Directory.Delete(dir, true); } catch (IOException) { } }
    }

    [Fact]
    public async Task Z3_PreparedChunk_OrderedAppend_MatchesPlainAppend()
    {
        var dir = TempDir();
        try
        {
            var payloads = new[] { Payload(1 << 20, 1), Payload(1 << 20, 2), Payload(1 << 20, 3) };
            var file = Path.Combine(dir, "prepared.orf");
            await using (var w = await OrfImage.CreateAsync(file, Spec(CompressionKind.Zstd)))
            {
                // prepare out of order (as parallel workers would), append in logical order
                var p1 = w.PrepareChunk(1, payloads[1]);
                var p0 = w.PrepareChunk(0, payloads[0]);
                var p2 = w.PrepareChunk(2, payloads[2]);
                AssertSuccess(await w.AppendPreparedAsync(0, p0));
                AssertSuccess(await w.AppendPreparedAsync(1, p1));
                AssertSuccess(await w.AppendPreparedAsync(2, p2));
                AssertSuccess(await w.CommitAsync());
            }
            var opened = await OrfImage.OpenAsync(file);
            await using var r = opened.Value;
            for (int i = 0; i < 3; i++)
            {
                var chunk = await r.ReadChunkAsync(i);
                AssertSuccess(chunk);
                Assert.Equal(payloads[i], chunk.Value);
            }
        }
        finally { try { Directory.Delete(dir, true); } catch (IOException) { } }
    }

    [Fact]
    public async Task Z4_OutOfOrder_Append_Refused()
    {
        var dir = TempDir();
        try
        {
            var file = Path.Combine(dir, "ooo.orf");
            await using (var w = await OrfImage.CreateAsync(file, Spec(CompressionKind.Zstd)))
            {
                AssertSuccess(await w.AppendPreparedAsync(5, w.PrepareChunk(5, new byte[64])));
                var refused = await w.AppendPreparedAsync(5, w.PrepareChunk(5, new byte[64]));
                Assert.False(refused.IsSuccess);
                Assert.Equal(ErrorCode.InvalidArgument, refused.Error.Code);
                AssertSuccess(await w.CommitAsync());
            }
        }
        finally { try { Directory.Delete(dir, true); } catch (IOException) { } }
    }
}
