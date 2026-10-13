using OpenReflect.Core;
using OpenReflect.Core.Abstractions;
using OpenReflect.Restore;
using OpenReflect.Verify;
using Xunit;

namespace OpenReflect.Tests;

/// <summary>Verify + Restore gates (S37–S42) on file fixtures (shared engine code paths with disks).</summary>
public class RestoreVerifyTests
{
    private const int Chunk = 64 * 1024;

    private static string TempDir()
    {
        var d = Path.Combine(Path.GetTempPath(), "orb-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(d);
        return d;
    }

    private static byte[] RandomBuf(int len, int seed) { var b = new byte[len]; new Random(seed).NextBytes(b); return b; }

    private static void AssertSuccess<T>(Result<T> r) where T : notnull
    {
        if (!r.IsSuccess) throw new Exception($"expected success but got: {r.Error}");
    }

    private static void AssertSuccess(Result r)
    {
        if (!r.IsSuccess) throw new Exception($"expected success but got: {r.Error}");
    }

    private static ImageOptions Opts(bool encrypt = false) => new()
    {
        ChunkSizeBytes = Chunk,
        Compression = CompressionKind.Deflate,
        Encryption = encrypt ? EncryptionKind.Aes256Gcm : EncryptionKind.None,
        KdfIterations = 100000,
        PasswordProvider = encrypt ? () => "round-trip-pw" : null,
    };

    private static byte[] MakeFixtureBytes()
    {
        var v = new byte[2 * Chunk + 1000];
        RandomBuf(Chunk, 10).CopyTo(v, 0);
        RandomBuf(1000, 11).CopyTo(v, 2 * Chunk);   // chunk 1 stays all-zero (sparse)
        return v;
    }

    private static async Task<Result<ImageHeaderInfo>> FullAsync(string src, string img, bool encrypt = false)
        => await new ImageEngine().CreateFullAsync(src, img, Opts(encrypt));

    [Fact]
    public async Task S37_Verify_ValidPasses_TamperedFails_NamingChunk()
    {
        var dir = TempDir();
        try
        {
            var fixture = Path.Combine(dir, "f.img");
            await File.WriteAllBytesAsync(fixture, MakeFixtureBytes());
            var image = Path.Combine(dir, "v.orf");
            AssertSuccess(await FullAsync(fixture, image));

            var verifier = new Verifier();
            var ok = await verifier.VerifyAsync(image, VerifyDepth.Full);
            Assert.True(ok.Success, string.Join("; ", ok.FailedChunks.Select(f => f.Reason)));
            Assert.Equal(2, ok.ChunksChecked);            // sparse chunk 1 not stored

            // tamper: flip one byte inside chunk 0's payload (header 128 + record overhead 72)
            var bytes = await File.ReadAllBytesAsync(image);
            bytes[128 + 72 + 5] ^= 0xFF;
            await File.WriteAllBytesAsync(image, bytes);

            var bad = await verifier.VerifyAsync(image, VerifyDepth.Full);
            Assert.False(bad.Success);
            Assert.Single(bad.FailedChunks);
            Assert.Equal(0, bad.FailedChunks[0].ChunkIndex);
        }
        finally { try { Directory.Delete(dir, true); } catch { } }
    }

    [Fact]
    public async Task S38_S39_Restore_Roundtrip_ByteIdentical()
    {
        var dir = TempDir();
        try
        {
            var fixture = Path.Combine(dir, "f.img");
            var expected = MakeFixtureBytes();
            await File.WriteAllBytesAsync(fixture, expected);
            var image = Path.Combine(dir, "r.orf");
            AssertSuccess(await FullAsync(fixture, image));

            var restorer = new Restorer();
            var target = Path.Combine(dir, "restored.img");
            var plan = await restorer.PlanAsync(image, target);
            AssertSuccess(plan);
            Assert.Equal(expected.Length, plan.Value.TargetSizeBytes);

            var exec = await restorer.ExecuteAsync(plan.Value, new RestoreOptions { DryRun = false, Confirmed = true });
            AssertSuccess(exec);

            var restored = await File.ReadAllBytesAsync(target);
            Assert.Equal(expected.Length, restored.Length);
            Assert.True(expected.AsSpan().SequenceEqual(restored), "restored bytes differ from source");
        }
        finally { try { Directory.Delete(dir, true); } catch { } }
    }

    [Fact]
    public async Task S40_IncrementalChain_MergeRestore_EqualsCurrentState()
    {
        var dir = TempDir();
        try
        {
            var fixture = Path.Combine(dir, "f.img");
            var v1 = MakeFixtureBytes();
            await File.WriteAllBytesAsync(fixture, v1);
            var rootImg = Path.Combine(dir, "root.orf");
            AssertSuccess(await FullAsync(fixture, rootImg));

            // mutate chunk 0 → incremental
            var delta = RandomBuf(Chunk, 21);
            await using (var fs = new FileStream(fixture, FileMode.Open, FileAccess.Write))
            {
                fs.Seek(0, SeekOrigin.Begin);
                fs.Write(delta);
            }
            var incImg = Path.Combine(dir, "inc.orf");
            AssertSuccess(await new ImageEngine().CreateIncrementalAsync(fixture, rootImg, incImg, Opts()));

            // restore the chain's newest state
            var restorer = new Restorer();
            var target = Path.Combine(dir, "merged.img");
            var plan = await restorer.PlanAsync(incImg, target);
            AssertSuccess(plan);
            Assert.Equal(2, plan.Value.ChainImages.Count);   // inc + root
            var exec = await restorer.ExecuteAsync(plan.Value, new RestoreOptions { DryRun = false, Confirmed = true });
            AssertSuccess(exec);

            var current = await File.ReadAllBytesAsync(fixture);
            var restored = await File.ReadAllBytesAsync(target);
            Assert.True(current.AsSpan().SequenceEqual(restored), "merged restore differs from current source state");
        }
        finally { try { Directory.Delete(dir, true); } catch { } }
    }

    [Fact]
    public async Task S41_EncryptedRoundtrip_RestoreFailsCleanlyWithWrongPassword()
    {
        var dir = TempDir();
        try
        {
            var fixture = Path.Combine(dir, "f.img");
            var expected = MakeFixtureBytes();
            await File.WriteAllBytesAsync(fixture, expected);
            var image = Path.Combine(dir, "enc.orf");
            AssertSuccess(await FullAsync(fixture, image, encrypt: true));

            var restorer = new Restorer();
            // planning needs the correct password (headers only), execution gets the WRONG one
            var plan = await restorer.PlanAsync(image, Path.Combine(dir, "out.img"), passwordProvider: () => "round-trip-pw");
            AssertSuccess(plan);
            var exec = await restorer.ExecuteAsync(plan.Value, new RestoreOptions
            {
                DryRun = false, Confirmed = true, PasswordProvider = () => "totally-wrong",
            });
            Assert.False(exec.IsSuccess);
            Assert.Equal(ErrorCode.WrongPassword, exec.Error.Code);   // fails fast at first chunk; no partial writes
            Assert.True(!File.Exists(Path.Combine(dir, "out.img")),
                "failed restore must roll back a newly-created empty target");
        }
        finally { try { Directory.Delete(dir, true); } catch { } }
    }

    [Fact]
    public async Task S42_SafetyGates_RefuseAsContracted()
    {
        var dir = TempDir();
        try
        {
            var fixture = Path.Combine(dir, "f.img");
            await File.WriteAllBytesAsync(fixture, MakeFixtureBytes());
            var image = Path.Combine(dir, "g.orf");
            AssertSuccess(await FullAsync(fixture, image));
            var restorer = new Restorer();

            // 1) dry-run writes nothing
            var absent = Path.Combine(dir, "dry.img");
            var plan = await restorer.PlanAsync(image, absent);
            AssertSuccess(plan);
            var dry = await restorer.ExecuteAsync(plan.Value, new RestoreOptions { DryRun = true, Confirmed = false });
            Assert.True(dry.IsSuccess);
            Assert.False(File.Exists(absent));

            // 2) unconfirmed execute → gate refusal
            var unconfirmed = await restorer.ExecuteAsync(plan.Value, new RestoreOptions { DryRun = false, Confirmed = false });
            Assert.False(unconfirmed.IsSuccess);
            Assert.Equal(ErrorCode.RestoreSafetyGateBlocked, unconfirmed.Error.Code);
            Assert.False(File.Exists(absent));

            // 3) physical-device targets are refused outright in v0.1 (incident lesson 2026-10-13:
            //    raw-device gates cannot be safely exercised against a live system disk — EVER)
            var sysPlan = await restorer.PlanAsync(image, @"\\.\PhysicalDrive0");
            AssertSuccess(sysPlan);
            var sys = await restorer.ExecuteAsync(sysPlan.Value, new RestoreOptions { DryRun = false, Confirmed = true });
            Assert.False(sys.IsSuccess);
            Assert.Equal(ErrorCode.RestoreSafetyGateBlocked, sys.Error.Code);
            var sysForced = await restorer.ExecuteAsync(sysPlan.Value, new RestoreOptions { DryRun = false, Confirmed = true, AllowOverwriteSystemDisk = true });
            Assert.False(sysForced.IsSuccess);
            Assert.Equal(ErrorCode.RestoreSafetyGateBlocked, sysForced.Error.Code);

            // 4) undersized existing target
            var small = Path.Combine(dir, "small.img");
            await File.WriteAllBytesAsync(small, new byte[100]);
            var smallPlan = await restorer.PlanAsync(image, small);
            AssertSuccess(smallPlan);
            var smallExec = await restorer.ExecuteAsync(smallPlan.Value, new RestoreOptions { DryRun = false, Confirmed = true });
            Assert.False(smallExec.IsSuccess);
            Assert.Equal(ErrorCode.RestoreTargetTooSmall, smallExec.Error.Code);
        }
        finally { try { Directory.Delete(dir, true); } catch { } }
    }
}
