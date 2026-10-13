using OpenReflect.Core;
using OpenReflect.Core.Abstractions;
using OpenReflect.Format;
using Xunit;

namespace OpenReflect.Tests;

/// <summary>Core engine suite (S19–S24, S29) on file fixtures — the full pipeline without real disks.</summary>
public class CoreTests
{
    private const int Chunk = 64 * 1024;

    private static void AssertSuccess<T>(Result<T> r) where T : notnull
    {
        if (!r.IsSuccess) throw new Exception($"expected success but got: {r.Error}");
    }

    private static string TempDir()
    {
        var d = Path.Combine(Path.GetTempPath(), "orb-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(d);
        return d;
    }

    private static byte[] RandomBuf(int len, int seed)
    {
        var b = new byte[len];
        new Random(seed).NextBytes(b);
        return b;
    }

    /// <summary>3 logical chunks: random | all-zero | random-short. Exercises skip + short tail.</summary>
    private static string MakeFixture(string dir, out byte[] expected)
    {
        var path = Path.Combine(dir, "fixture.img");
        using var fs = new FileStream(path, FileMode.Create, FileAccess.Write);
        var c0 = RandomBuf(Chunk, 1);
        var zeros = new byte[Chunk];
        var c2 = RandomBuf(1000, 2);
        fs.Write(c0); fs.Write(zeros); fs.Write(c2);
        expected = new byte[c0.Length + zeros.Length + c2.Length];
        c0.CopyTo(expected, 0); zeros.CopyTo(expected, c0.Length); c2.CopyTo(expected, c0.Length + zeros.Length);
        return path;
    }

    private static ImageOptions Opts(bool encrypt = false, bool deflate = true) => new()
    {
        ChunkSizeBytes = Chunk,
        Compression = deflate ? CompressionKind.Deflate : CompressionKind.None,
        CompressionLevel = 6,
        Encryption = encrypt ? EncryptionKind.Aes256Gcm : EncryptionKind.None,
        KdfIterations = 100000,
        PasswordProvider = encrypt ? () => "test-password" : null,
        SkipZeroRegions = true,
    };

    [Fact]
    public async Task S21_RawRead_MatchesFixtureBytes()
    {
        var dir = TempDir();
        try
        {
            var fixture = MakeFixture(dir, out var expected);
            var provider = new PassThroughSnapshotProvider();
            var session = await provider.AcquireAsync(fixture, allowRawFallback: true);
            AssertSuccess(session);
            await using var s = session.Value;
            using var stream = s.OpenReadStream();
            var head = new byte[512];
            Assert.Equal(512, stream.Read(head, 0, 512));
            Assert.Equal(expected.AsSpan(0, 512).ToArray(), head);

            var missing = await provider.AcquireAsync(Path.Combine(dir, "nope.img"), true);
            Assert.False(missing.IsSuccess);
            Assert.Equal(ErrorCode.SourceNotFound, missing.Error.Code);
        }
        finally { try { Directory.Delete(dir, true); } catch { } }
    }

    [Fact]
    public async Task S24_FullImage_E2E_HashesMatch()
    {
        var dir = TempDir();
        try
        {
            var fixture = MakeFixture(dir, out var expected);
            var image = Path.Combine(dir, "full.orf");
            var engine = new ImageEngine();
            var result = await engine.CreateFullAsync(fixture, image, Opts(encrypt: true, deflate: true));
            AssertSuccess(result);
            Assert.True(result.Value.IsComplete);
            Assert.Equal((long)(2 * Chunk + 1000), result.Value.LogicalSizeBytes);   // exact source size from footer

            var opened = await OrfImage.OpenAsync(image, password: "test-password");
            AssertSuccess(opened);
            await using var r = opened.Value;
            Assert.True(r.Header.Encrypted);

            long stored = 0;
            for (long logical = 0; logical < 3; logical++)
            {
                if (!r.HasChunk(logical)) continue;      // chunk 1 was all-zero → omitted (S22)
                stored++;
                var chunk = await r.ReadChunkAsync(logical);
                AssertSuccess(chunk);
                var want = expected.AsSpan((int)(logical * Chunk), (int)Math.Min(Chunk, expected.Length - logical * Chunk)).ToArray();
                Assert.Equal(want, chunk.Value);
            }
            Assert.Equal(2, stored);                     // zeros skipped
        }
        finally { try { Directory.Delete(dir, true); } catch { } }
    }

    [Fact]
    public async Task S22_ZeroRegion_Omitted_ButRestoreSemanticsHold()
    {
        var dir = TempDir();
        try
        {
            var fixture = MakeFixture(dir, out _);
            var image = Path.Combine(dir, "sparse.orf");
            var engine = new ImageEngine();
            var result = await engine.CreateFullAsync(fixture, image, Opts());
            AssertSuccess(result);

            var opened = await OrfImage.OpenAsync(image);
            await using var r = opened.Value;
            Assert.True(r.HasChunk(0));
            Assert.False(r.HasChunk(1));                 // zero region omitted
            Assert.True(r.HasChunk(2));
            Assert.False(r.Header.Encrypted);
        }
        finally { try { Directory.Delete(dir, true); } catch { } }
    }

    [Fact]
    public async Task S23_Resume_AbandonedImage_ContinuesAndCommits()
    {
        var dir = TempDir();
        try
        {
            var fixture = MakeFixture(dir, out var expected);
            var image = Path.Combine(dir, "resume.orf");

            // simulate a crash after 2 chunks: writer abandoned mid-stream
            var w = await OrfImage.CreateAsync(image, new OrfCreateSpec
            {
                ImageId = Guid.NewGuid(), ChainKind = ChainKind.Full, ChunkSizeBytes = Chunk,
                SourceLabel = "fixture", Compression = CompressionKind.Deflate, CompressionLevel = 6,
                LogicalSizeBytes = expected.Length,
            });
            await using (w)
            {
                Assert.True((await w.AppendChunkAsync(0, expected.AsMemory(0, Chunk))).IsSuccess);
                Assert.True((await w.AppendChunkAsync(1, expected.AsMemory(Chunk, Chunk))).IsSuccess);
                await w.AbandonAsync();                   // dirty flag stays; footer never written
            }

            var resumed = await OrfImage.ResumeAsync(image);
            AssertSuccess(resumed);
            await using (var w2 = resumed.Value)
            {
                Assert.True((await w2.AppendChunkAsync(2, expected.AsMemory(2 * Chunk, expected.Length - 2 * Chunk))).IsSuccess);
                var committed = await w2.CommitAsync();
                AssertSuccess(committed);
                Assert.True(committed.Value.IsComplete);
            }

            var opened = await OrfImage.OpenAsync(image);
            Assert.True(opened.IsSuccess);
            await using (var r = opened.Value)
            {
                Assert.True(r.Header.IsComplete);
                var c0 = await r.ReadChunkAsync(0);
                var c1 = await r.ReadChunkAsync(1);
                var c2 = await r.ReadChunkAsync(2);
                Assert.True(c0.IsSuccess && c1.IsSuccess && c2.IsSuccess);
                Assert.Equal(expected[..Chunk], c0.Value);
                Assert.Equal(expected[Chunk..(2 * Chunk)], c1.Value);
                Assert.Equal(expected[(2 * Chunk)..], c2.Value);
            }
        }
        finally { try { Directory.Delete(dir, true); } catch { } }
    }

    [Fact]
    public async Task S29_Incremental_And_Differential_Chains()
    {
        var dir = TempDir();
        try
        {
            var fixture = MakeFixture(dir, out var v1);
            var rootImg = Path.Combine(dir, "root.orf");
            var engine = new ImageEngine();
            var rootRes = await engine.CreateFullAsync(fixture, rootImg, Opts());
            AssertSuccess(rootRes);

            // mutate chunk 1 (was zero) → incremental must store ONLY chunk 1
            await using (var fs = new FileStream(fixture, FileMode.Open, FileAccess.Write))
            {
                fs.Seek(Chunk, SeekOrigin.Begin);
                fs.Write(RandomBuf(Chunk, 3));
            }
            var incImg = Path.Combine(dir, "inc.orf");
            var incRes = await engine.CreateIncrementalAsync(fixture, rootImg, incImg, Opts());
            AssertSuccess(incRes);
            var openedInc = await OrfImage.OpenAsync(incImg);
            await using (var ri = openedInc.Value)
            {
                Assert.Equal(ChainKind.Incremental, ri.Header.ChainKind);
                Assert.Equal(rootRes.Value.ImageId, ri.Header.ParentImageId);
                Assert.False(ri.HasChunk(0));            // unchanged vs parent
                Assert.True(ri.HasChunk(1));             // changed
                Assert.False(ri.HasChunk(2));            // unchanged vs parent
            }

            // mutate chunk 2 → differential vs ROOT must store only chunk 2 (chunk 1 still equals ROOT: root omitted it as zeros? no —
            // root's effective chunk 1 is zeros, current chunk 1 is random → differential MUST store chunk 1 as well)
            await using (var fs = new FileStream(fixture, FileMode.Open, FileAccess.Write))
            {
                fs.Seek(2 * Chunk, SeekOrigin.Begin);
                fs.Write(RandomBuf(1000, 4));
            }
            var diffImg = Path.Combine(dir, "diff.orf");
            var diffRes = await engine.CreateDifferentialAsync(fixture, rootImg, diffImg, Opts());
            AssertSuccess(diffRes);
            var openedDiff = await OrfImage.OpenAsync(diffImg);
            await using (var rd = openedDiff.Value)
            {
                Assert.Equal(ChainKind.Differential, rd.Header.ChainKind);
                Assert.Equal(rootRes.Value.ImageId, rd.Header.ParentImageId);
                Assert.False(rd.HasChunk(0));            // equals root (root stores it)
                Assert.True(rd.HasChunk(1));             // differs from root's effective zeros
                Assert.True(rd.HasChunk(2));             // differs from root
            }
        }
        finally { try { Directory.Delete(dir, true); } catch { } }
    }

    [Fact]
    public async Task S20_SnapshotOrchestration_Seam()
    {
        var dir = TempDir();
        try
        {
            // deferred VSS provider yields the typed VssFailure (documented 0.2 deferral, RISKS R1)
            var vss = new DeferredVssSnapshotProvider();
            var fail = await vss.AcquireAsync(@"\\.\PhysicalDrive9", allowRawFallback: false);
            Assert.False(fail.IsSuccess);
            Assert.Equal(ErrorCode.VssFailure, fail.Error.Code);

            // the engine honors a failing provider through the seam
            var engine = new ImageEngine(vss);
            var fixture = MakeFixture(dir, out _);
            var res = await engine.CreateFullAsync(fixture, Path.Combine(dir, "x.orf"), Opts());
            Assert.False(res.IsSuccess);
            Assert.Equal(ErrorCode.VssFailure, res.Error.Code);

            // default engine (PassThrough) works
            var engine2 = new ImageEngine();
            var ok = await engine2.CreateFullAsync(fixture, Path.Combine(dir, "y.orf"), Opts());
            AssertSuccess(ok);
        }
        finally { try { Directory.Delete(dir, true); } catch { } }
    }

    [Fact]
    public void S19S33_JobConfig_Validation()
    {
        var dir = TempDir();
        try
        {
            var good = Path.Combine(dir, "good.job.json");
            File.WriteAllText(good, """
            {
              "schemaVersion": 1,
              "jobId": "6f0e2a5c-9d1a-4c3e-b7a2-2f8d1e5c9b01",
              "name": "test job",
              "source": { "path": "C:\\\\some\\\\fixture.img" },
              "destination": { "directory": "C:\\\\some\\\\dest", "fileNamePrefix": "sys" },
              "chain": { "kind": "incremental" },
              "options": { "chunkSizeBytes": 4194304, "compression": "deflate", "compressionLevel": 6 },
              "unknownFutureField": { "ignored": true }
            }
            """);
            var loaded = JobConfig.Load(good);
            AssertSuccess(loaded);
            Assert.Equal("incremental", loaded.Value.Chain.Kind);

            var v2 = Path.Combine(dir, "v2.job.json");
            File.WriteAllText(v2, """{ "schemaVersion": 2, "source": { "path": "x" }, "destination": { "directory": "y" } }""");
            Assert.Equal(ErrorCode.NotSupported, JobConfig.Load(v2).Error.Code);

            var badChunk = Path.Combine(dir, "bad.job.json");
            File.WriteAllText(badChunk, """{ "schemaVersion": 1, "source": { "path": "x" }, "destination": { "directory": "y" }, "options": { "chunkSizeBytes": 12345 } }""");
            Assert.Equal(ErrorCode.InvalidArgument, JobConfig.Load(badChunk).Error.Code);

            var noPw = Path.Combine(dir, "nopw.job.json");
            File.WriteAllText(noPw, """{ "schemaVersion": 1, "source": { "path": "x" }, "destination": { "directory": "y" }, "options": { "encryption": "aes256gcm" } }""");
            Assert.Equal(ErrorCode.InvalidArgument, JobConfig.Load(noPw).Error.Code);
        }
        finally { try { Directory.Delete(dir, true); } catch { } }
    }
}
