using OpenReflect.Core.Abstractions;
using OpenReflect.Format;
using Xunit;

namespace OpenReflect.Tests;

/// <summary>Format-layer conformance suite (S25–S30) against docs/FORMAT.md.</summary>
public class FormatTests
{
    private static string TempDir()
    {
        var d = Path.Combine(Path.GetTempPath(), "orf-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(d);
        return d;
    }

    private static OrfCreateSpec Spec(ChainKind kind = ChainKind.Full, long chunkSize = 1024,
        CompressionKind comp = CompressionKind.None, EncryptionKind enc = EncryptionKind.None, string? pw = null) => new()
    {
        ImageId = Guid.NewGuid(),
        ParentImageId = kind == ChainKind.Full ? Guid.Empty : Guid.Parse("11111111-2222-3333-4444-555555555555"),
        ChainKind = kind,
        ChunkSizeBytes = chunkSize,
        SourceLabel = "TESTDISK",
        Compression = comp,
        CompressionLevel = 6,
        Encryption = enc,
        KdfIterations = 100000,      // fast iterations for tests only; production default is 600k
        Password = pw,
    };

    private static byte[] Chunk(long seed, int len)
    {
        var b = new byte[len];
        new Random((int)seed).NextBytes(b);
        return b;
    }

    private static async Task<OrfWriter> NewWriterAsync(string path, OrfCreateSpec spec)
        => await OrfImage.CreateAsync(path, spec);

    [Fact]
    public async Task S25_HeaderAndRecordRoundtrip_AllFields()
    {
        var dir = TempDir();
        try
        {
            var file = Path.Combine(dir, "a.orf");
            var chunks = new[] { Chunk(1, 1024), Chunk(2, 1024), Chunk(3, 700) };
            var spec = Spec(chunkSize: 1024);
            await using (var w = await NewWriterAsync(file, spec))
            {
                for (long i = 0; i < chunks.Length; i++)
                    Assert.True((await w.AppendChunkAsync(i, chunks[i])).IsSuccess);
                var committed = await w.CommitAsync();
                Assert.True(committed.IsSuccess);
                Assert.Equal(3, committed.Value.ChunkCount);
                Assert.True(committed.Value.IsComplete);
            }
            var opened = await OrfImage.OpenAsync(file);
            AssertSuccess(opened);
            var r = opened.Value;
            try {
            Assert.Equal(spec.ImageId, r.Header.ImageId);
            Assert.Equal(Guid.Empty, r.Header.ParentImageId);
            Assert.Equal(ChainKind.Full, r.Header.ChainKind);
            Assert.Equal(1024, r.Header.ChunkSizeBytes);
            Assert.Equal("TESTDISK", r.Header.SourceLabel);
            Assert.True(r.Header.IsComplete);
            Assert.Equal(3, r.Header.ChunkCount);
            Assert.Equal(1024 * 3, r.Header.LogicalSizeBytes);   // chunk grid; final chunk may be short
            for (long i = 0; i < chunks.Length; i++)
            {
                var got = await r.ReadChunkAsync(i);
                AssertSuccess(got);
                Assert.Equal(chunks[i], got.Value);
            }
            var off = await r.GetChunkFileOffsetAsync(0);
            Assert.True(off.IsSuccess);
            Assert.Equal(128, off.Value);          // first record directly after header
            } finally { await r.DisposeAsync(); }
        }
        finally { RetryDelete(dir); }
    }

    [Fact]
    public async Task S26_GoldenIndexManifestIntegrity()
    {
        var dir = TempDir();
        try
        {
            var file = Path.Combine(dir, "g.orf");
            var payload = new byte[1024];
            new Random(42).NextBytes(payload);
            await using (var w = await NewWriterAsync(file, Spec()))
            {
                for (long i = 0; i < 3; i++) Assert.True((await w.AppendChunkAsync(i, payload)).IsSuccess);
                var c = await w.CommitAsync();
                Assert.True(c.IsSuccess);
            }

            // Recompute the manifest from the on-disk index exactly as FORMAT.md §5 defines it.
            var bytes = await File.ReadAllBytesAsync(file);
            long footerOffset = bytes.Length - 64;
            Assert.Equal("ORFF", System.Text.Encoding.ASCII.GetString(bytes, (int)footerOffset, 4));
            uint crcStored = BitConverter.ToUInt32(bytes, (int)footerOffset + 0x28);
            var footerRegion = new byte[0x28];
            Array.Copy(bytes, footerOffset, footerRegion, 0, 0x28);
            Assert.Equal(Crc32(footerRegion), crcStored);

            long indexOffset = BitConverter.ToInt64(bytes, (int)footerOffset + 0x08);
            long indexLength = BitConverter.ToInt64(bytes, (int)footerOffset + 0x10);
            long manifestOffset = BitConverter.ToInt64(bytes, (int)footerOffset + 0x18);
            var idx = new byte[indexLength];
            Array.Copy(bytes, indexOffset, idx, 0, indexLength);
            Assert.Equal(24 + 2 * 16, indexLength);            // count + 3 entries
            using var sha = System.Security.Cryptography.SHA256.Create();
            var manifestOnDisk = new byte[32];
            Array.Copy(bytes, manifestOffset, manifestOnDisk, 0, 32);
            Assert.Equal(sha.ComputeHash(idx), manifestOnDisk);

            // Footerless/incomplete file must not claim completion
            var truncated = Path.Combine(dir, "t.orf");
            await File.WriteAllBytesAsync(truncated, bytes[..(int)indexOffset]);
            var openedT = await OrfImage.OpenAsync(truncated);
            Assert.True(openedT.IsSuccess);
            await using (var rt = openedT.Value)
            {
                Assert.False(rt.Header.IsComplete);
                Assert.Equal(3, rt.Header.ChunkCount);  // salvage scan finds all records
            }
        }
        finally { RetryDelete(dir); }
    }

    [Fact]
    public async Task S27_CompressionRoundtrip_WithRatio()
    {
        var dir = TempDir();
        try
        {
            var file = Path.Combine(dir, "c.orf");
            var repetitive = new byte[4096];
            for (int i = 0; i < repetitive.Length; i++) repetitive[i] = (byte)(i % 7);
            await using (var w = await NewWriterAsync(file, Spec(comp: CompressionKind.Deflate)))
            {
                Assert.True((await w.AppendChunkAsync(0, repetitive)).IsSuccess);
                Assert.True((await w.CommitAsync()).IsSuccess);
            }
            var size = new FileInfo(file).Length;
            Assert.True(size < 128 + 72 + 4096, $"compressed file ({size}B) should be far smaller than raw+overhead");
            var opened = await OrfImage.OpenAsync(file);
            await using var r = opened.Value;
            var got = await r.ReadChunkAsync(0);
            Assert.True(got.IsSuccess);
            Assert.Equal(repetitive, got.Value);
        }
        finally { RetryDelete(dir); }
    }

    [Fact]
    public async Task S28_EncryptionRoundtrip_WrongPasswordFails()
    {
        var dir = TempDir();
        try
        {
            var file = Path.Combine(dir, "e.orf");
            var secret = Chunk(7, 1024);
            await using (var w = await NewWriterAsync(file, Spec(enc: EncryptionKind.Aes256Gcm, pw: "correct horse")))
            {
                Assert.True((await w.AppendChunkAsync(0, secret)).IsSuccess);
                Assert.True((await w.CommitAsync()).IsSuccess);
            }

            var ok = await OrfImage.OpenAsync(file, password: "correct horse");
            AssertSuccess(ok);
            await using (var r = ok.Value)
            {
                Assert.True(r.Header.Encrypted, "committed header lacks encrypted flag");
                var got = await r.ReadChunkAsync(0);
                AssertSuccess(got);
                Assert.Equal(secret, got.Value);
            }

            var bad = await OrfImage.OpenAsync(file, password: "wrong");
            await using (var r = bad.Value)
            {
                var got = await r.ReadChunkAsync(0);
                Assert.False(got.IsSuccess);
                Assert.Equal(ErrorCode.WrongPassword, got.Error.Code);
            }

            var noPw = await OrfImage.OpenAsync(file);
            Assert.False(noPw.IsSuccess);
            Assert.Equal(ErrorCode.WrongPassword, noPw.Error.Code);
        }
        finally { RetryDelete(dir); }
    }

    [Fact]
    public async Task S29_ChainMetadata_ParentLinkRoundtrip()
    {
        var dir = TempDir();
        try
        {
            var parent = Guid.NewGuid();
            var file = Path.Combine(dir, "inc.orf");
            await using (var w = await NewWriterAsync(file, Spec(kind: ChainKind.Incremental, pw: null)))
            {
                w.GetType();
                Assert.True((await w.AppendChunkAsync(0, new byte[1024])).IsSuccess);
                Assert.True((await w.CommitAsync()).IsSuccess);
            }
            var opened = await OrfImage.OpenAsync(file);
            await using var r = opened.Value;
            Assert.Equal(ChainKind.Incremental, r.Header.ChainKind);
            Assert.Equal(parent == Guid.Empty ? Guid.Empty : Guid.Parse("11111111-2222-3333-4444-555555555555"), r.Header.ParentImageId);
        }
        finally { RetryDelete(dir); }
    }

    [Fact]
    public async Task S30_Conformance_Negatives()
    {
        var dir = TempDir();
        try
        {
            // garbage file → bad magic
            var junk = Path.Combine(dir, "junk.orf");
            await File.WriteAllBytesAsync(junk, new byte[500]);
            var r1 = await OrfImage.OpenAsync(junk);
            Assert.Equal(ErrorCode.BadFormat, r1.Error.Code);

            // good image, then corrupt header body → header CRC failure
            var good = Path.Combine(dir, "good.orf");
            await using (var w = await NewWriterAsync(good, Spec()))
            {
                Assert.True((await w.AppendChunkAsync(0, new byte[1024])).IsSuccess);
                Assert.True((await w.CommitAsync()).IsSuccess);
            }
            var corrupted = Path.Combine(dir, "corrupt.orf");
            var bytes = await File.ReadAllBytesAsync(good);
            bytes[0x12] ^= 0xFF;                                // inside imageId, before CRC field
            await File.WriteAllBytesAsync(corrupted, bytes);
            var r2 = await OrfImage.OpenAsync(corrupted);
            Assert.Equal(ErrorCode.CorruptHeader, r2.Error.Code);

            // corrupt a byte inside the INDEX → manifest mismatch
            var idxCorrupt = Path.Combine(dir, "idxcorrupt.orf");
            var b2 = await File.ReadAllBytesAsync(good);
            long footerOffset = b2.Length - 64;
            long indexOffset = BitConverter.ToInt64(b2, (int)footerOffset + 0x08);
            b2[indexOffset + 9] ^= 0x01;                        // inside first index entry
            await File.WriteAllBytesAsync(idxCorrupt, b2);
            var r3 = await OrfImage.OpenAsync(idxCorrupt);
            Assert.Equal(ErrorCode.CorruptChunk, r3.Error.Code);
            Assert.Contains("manifest", r3.Error.Message, StringComparison.OrdinalIgnoreCase);

            // unsupported version
            var version = Path.Combine(dir, "ver.orf");
            var b3 = await File.ReadAllBytesAsync(good);
            b3[4] = 99;
            await File.WriteAllBytesAsync(version, b3);
            var r4 = await OrfImage.OpenAsync(version);
            Assert.Equal(ErrorCode.UnsupportedVersion, r4.Error.Code);

            // abandoned writer leaves dirty flag; salvage reads still work
            var abandoned = Path.Combine(dir, "ab.orf");
            var w2 = await NewWriterAsync(abandoned, Spec());
            Assert.True((await w2.AppendChunkAsync(0, new byte[1024])).IsSuccess);
            await w2.AbandonAsync();
            var r5 = await OrfImage.OpenAsync(abandoned);
            Assert.True(r5.IsSuccess);
            await using (var rr = r5.Value)
            {
                Assert.False(rr.Header.IsComplete);
                var chunk = await rr.ReadChunkAsync(0);
                Assert.True(chunk.IsSuccess);                   // salvage read works
            }

            // missing parent region read → typed failure
            await using (var rr = r5.Value)
            {
                var missing = await rr.ReadChunkAsync(5);
                Assert.False(missing.IsSuccess);
                Assert.Equal(ErrorCode.BadFormat, missing.Error.Code);
            }
        }
        finally { RetryDelete(dir); }
    }

    [Fact]
    public async Task Probe_HandleReleased_AfterEachStage()
    {
        var dir = TempDir();
        var file = Path.Combine(dir, "p.orf");
        var w = await OrfImage.CreateAsync(file, Spec());
        Assert.True((await w.AppendChunkAsync(0, new byte[1024])).IsSuccess);
        var c = await w.CommitAsync();
        Assert.True(c.IsSuccess);
        await w.DisposeAsync();                    // writer dispose AFTER commit
        AssertCanOpenExclusive(file, "after-writer-dispose-postcommit");
        var opened = await OrfImage.OpenAsync(file);
        Assert.True(opened.IsSuccess);
        var r = opened.Value;
        var got = await r.ReadChunkAsync(0);
        Assert.True(got.IsSuccess);
        AssertCannotOpenExclusive(file, "while-reader-open");
        await r.DisposeAsync();
        AssertCanOpenExclusive(file, "after-reader-dispose");
        Directory.Delete(dir, true);
    }

    private static void AssertCanOpenExclusive(string file, string stage)
    {
        try { File.Open(file, FileMode.Open, FileAccess.ReadWrite, FileShare.None).Dispose(); }
        catch (IOException ex) { throw new IOException($"HANDLE HELD [{stage}]: {ex.Message}"); }
    }

    private static void AssertCannotOpenExclusive(string file, string stage)
    {
        try { File.Open(file, FileMode.Open, FileAccess.ReadWrite, FileShare.None).Dispose(); Assert.Fail($"[{stage}] file unexpectedly free"); }
        catch (IOException) { /* expected: a legitimate handle is open */ }
    }

    /// <summary>Best-effort temp cleanup. Functional gates above are the assertions; on this machine
    /// (TEMP=C:\Temp) an external scanner can hold fresh files open briefly, so an undeletable file
    /// is renamed aside instead of failing the test.</summary>
    private static void AssertSuccess<T>(Result<T> r) where T : notnull
    {
        if (!r.IsSuccess) throw new Exception($"expected success but got: {r.Error}");
    }

    private static void RetryDelete(string dir)
    {
        for (int attempt = 0; attempt < 4; attempt++)
        {
            try { Directory.Delete(dir, recursive: true); return; }
            catch (DirectoryNotFoundException) { return; }
            catch (IOException) { Thread.Sleep(50); }
            catch (UnauthorizedAccessException) { break; }
        }
        try
        {
            foreach (var f in Directory.GetFiles(dir))
                try { File.Move(f, f + ".locked"); } catch { }
            Directory.Delete(dir, recursive: true);
        }
        catch { /* leave for OS temp cleanup */ }
    }

    private static uint Crc32(byte[] data)
    {
        uint crc = 0xFFFFFFFFu;
        foreach (var b in data)
        {
            crc ^= b;
            for (int i = 0; i < 8; i++) crc = (crc >> 1) ^ (0xEDB88320u & (uint)(-(int)(crc & 1)));
        }
        return ~crc;
    }
}

public class CryptoProbeTests
{
    [Fact]
    public void Probe_TransformsDirectRoundtrip()
    {
        var salt = new byte[16]; Random.Shared.NextBytes(salt);
        var prefix = new byte[4]; Random.Shared.NextBytes(prefix);
        var key = OpenReflect.Format.Transforms.DeriveKey("correct horse", salt, 100000);
        var data = new byte[1024]; Random.Shared.NextBytes(data);
        var payload = OpenReflect.Format.Transforms.EncryptChunk(key, prefix, 0, data);
        var back = OpenReflect.Format.Transforms.TryDecryptChunk(key, prefix, 0, payload, data.Length);
        Assert.NotNull(back);
        Assert.Equal(data, back);
    }

    [Fact]
    public async Task Probe_HeaderParamsVsWriterSpec()
    {
        var dir = Path.Combine(Path.GetTempPath(), "orf-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        try
        {
            var file = Path.Combine(dir, "x.orf");
            var data = new byte[1024]; Random.Shared.NextBytes(data);
            var spec = new OrfCreateSpec { ImageId = Guid.NewGuid(), ChainKind = ChainKind.Full, ChunkSizeBytes = 1024,
                SourceLabel = "T", Encryption = EncryptionKind.Aes256Gcm, KdfIterations = 100000, Password = "correct horse" };
            await using (var w = await OrfImage.CreateAsync(file, spec))
            {
                Assert.True((await w.AppendChunkAsync(0, data)).IsSuccess);
                Assert.True((await w.CommitAsync()).IsSuccess);
            }
            var raw = await File.ReadAllBytesAsync(file);
            var saltOnDisk = raw[0x5C..0x6C];
            var nonceOnDisk = raw[0x6C..0x70];
            uint itersOnDisk = BitConverter.ToUInt32(raw, 0x58);
            ushort flagsOnDisk = BitConverter.ToUInt16(raw, 0x2E);
            var keyFromDisk = OpenReflect.Format.Transforms.DeriveKey("correct horse", saltOnDisk, (int)itersOnDisk);
            long recOff = 128;
            var recHdr = raw[(int)recOff..(int)(recOff + 72)];
            long storedLen = BitConverter.ToInt64(recHdr, 0x18);
            ushort recFlags = BitConverter.ToUInt16(recHdr, 0x20);
            var payload = raw[(int)(recOff + 72)..(int)(recOff + 72 + storedLen)];
            var dec = OpenReflect.Format.Transforms.TryDecryptChunk(keyFromDisk, nonceOnDisk, 0, payload, 1024);
            Assert.True(dec != null, $"flags={flagsOnDisk} recFlags={recFlags} iters={itersOnDisk} salt={Convert.ToHexString(saltOnDisk)} nonce={Convert.ToHexString(nonceOnDisk)} storedLen={storedLen}");
            Assert.Equal(data, dec);
        }
        finally { try { Directory.Delete(dir, true); } catch { } }
    }
}
