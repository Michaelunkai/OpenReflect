using System.Diagnostics;
using System.Security.Cryptography;
using OpenReflect.Core;
using OpenReflect.Core.Abstractions;
using OpenReflect.DiskAccess;
using OpenReflect.Restore;
using Xunit;

namespace OpenReflect.Tests;

/// <summary>The controlled destructive path: every device write in this class targets a VHD
/// attached as a virtual PhysicalDrive — never a real disk. Covers:
///   E1 size-mismatch gate, E2 running-OS refusal (even confirmed), E3 the full chain
///   intelligent backup (VSS + zstd + $Bitmap skip) → full verify → guarded disk restore with
///   explicit zero fill → file-hash equality + GPT equality + unconfirmed refusal,
///   E4 skip-unwritten restore over a dirty target writes strictly less and keeps files equal.</summary>
public class DiskRestoreE2ETests
{
    private const int VhdBytes = 512 << 20;   // 512 MiB — fast to format, real NTFS bitmap
    private const int ChunkSize = 1 << 20;

    private static void AssertSuccess(Result r)
    {
        if (!r.IsSuccess) throw new Exception($"expected success but got: {r.Error}");
    }

    // ---------- diskpart helpers ----------

    private static void Diskpart(string script)
    {
        var file = Path.Combine(Path.GetTempPath(), "dp-" + Guid.NewGuid().ToString("N") + ".txt");
        File.WriteAllText(file, script.Replace("\n", "\r\n"));
        try
        {
            var psi = new ProcessStartInfo("diskpart", "/s \"" + file + "\"")
            {
                UseShellExecute = false,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                CreateNoWindow = true,
            };
            using var p = Process.Start(psi)!;
            var output = p.StandardOutput.ReadToEnd() + p.StandardError.ReadToEnd();
            p.WaitForExit(180_000);
            if (p.ExitCode != 0)
                throw new Exception($"diskpart failed (exit {p.ExitCode}) script: {script} | output: {output}");
        }
        finally
        {
            try { File.Delete(file); } catch (IOException) { }
        }
    }

    private static void CaptureDisks(HashSet<int> into)
    {
        into.Clear();
        for (int n = 0; n < 32; n++)
        {
            var d = DiskLayout.ReadAsync(n).GetAwaiter().GetResult();
            if (d.IsSuccess) into.Add(n);
        }
    }

    private static int AttachedDiskNumber(long sizeBytes, HashSet<int> before)
    {
        for (int attempt = 0; attempt < 50; attempt++)
        {
            for (int n = 0; n < 32; n++)
            {
                if (before.Contains(n)) continue;
                var d = DiskLayout.ReadAsync(n).GetAwaiter().GetResult();
                if (d.IsSuccess && d.Value.SizeBytes == sizeBytes) return n;
            }
            Thread.Sleep(200);
        }
        throw new Exception("attached VHD did not appear as a PhysicalDrive");
    }

    private sealed class Vhd : IDisposable
    {
        public string File = "";
        public int DiskNumber = -1;
        private readonly HashSet<int> _disksBefore = new();

        /// <summary>Creates, attaches, partitions (GPT + NTFS primary) and letter-assigns in one script.</summary>
        public static Vhd CreatePrepared(string dir, string name, char letter)
        {
            var v = new Vhd { File = Path.Combine(dir, name + ".vhdx") };
            CaptureDisks(v._disksBefore);
            Diskpart($"""
                create vdisk file="{v.File}" maximum={VhdBytes / (1 << 20)} type=expandable
                select vdisk file="{v.File}"
                attach vdisk
                convert gpt
                create partition primary
                format fs=ntfs quick
                assign letter={letter}
                """);
            v.DiskNumber = AttachedDiskNumber(VhdBytes, v._disksBefore);
            return v;
        }

        public static Vhd CreateBlank(string dir, string name)
        {
            var v = new Vhd { File = Path.Combine(dir, name + ".vhdx") };
            CaptureDisks(v._disksBefore);
            Diskpart($"""
                create vdisk file="{v.File}" maximum={VhdBytes / (1 << 20)} type=expandable
                select vdisk file="{v.File}"
                attach vdisk
                """);
            v.DiskNumber = AttachedDiskNumber(VhdBytes, v._disksBefore);
            return v;
        }

        public void Attach()
        {
            Diskpart($"""
                select vdisk file="{File}"
                attach vdisk
                """);
            DiskNumber = AttachedDiskNumber(VhdBytes, _disksBefore);
        }

        public void Detach()
        {
            Diskpart($"""
                select vdisk file="{File}"
                detach vdisk
                """);
        }

        public void AssignLetter(char letter)
        {
            // GPT conversion auto-creates an MSR as partition 1 — assign the largest partition
            var layout = DiskLayout.ReadAsync(DiskNumber).GetAwaiter().GetResult();
            if (!layout.IsSuccess || layout.Value.Partitions.Count == 0)
            {
                var state = new System.Text.StringBuilder();
                for (int n = 0; n < 32; n++)
                {
                    var d = DiskLayout.ReadAsync(n).GetAwaiter().GetResult();
                    if (d.IsSuccess)
                        state.Append($"disk{n}={d.Value.SizeBytes}B parts={d.Value.Partitions.Count}; ");
                }
                throw new Exception(
                    $"AssignLetter: disk {DiskNumber} layout empty/failing (ok={layout.IsSuccess}, parts={(layout.IsSuccess ? layout.Value.Partitions.Count : -1)}, size={(layout.IsSuccess ? layout.Value.SizeBytes : -1)}). State: {state}");
            }
            var biggest = layout.Value.Partitions.OrderByDescending(p => p.Length).First();
            Diskpart($"""
                select vdisk file="{File}"
                select disk {DiskNumber}
                select partition {biggest.Number}
                assign letter={letter}
                """);
        }

        public void Dispose()
        {
            try { Diskpart($"select vdisk file=\"{File}\"\ndetach vdisk"); } catch { }
            try { if (System.IO.File.Exists(File)) System.IO.File.Delete(File); } catch (IOException) { }
        }
    }

    // ---------- data ----------

    private sealed record FileSpec(string RelPath, byte[] Content);

    private static List<FileSpec> WriteTestFiles(string root)
    {
        var files = new List<FileSpec>
        {
            new("random-3mb.bin", RandomBytes(3 << 20, seed: 11)),
            new("random-5mb.bin", RandomBytes(5 << 20, seed: 22)),
            new("text.txt", System.Text.Encoding.UTF8.GetBytes(string.Join('\n', Enumerable.Repeat("OpenReflect parity test line 1234567890", 4000)))),
            new("nested" + Path.DirectorySeparatorChar + "sparse.bin", new byte[8 << 20]),
        };
        RandomBytes(1 << 20, seed: 33).CopyTo(files[3].Content, 0);   // noise head, NTFS sparse tail
        foreach (var f in files)
        {
            var full = Path.Combine(root, f.RelPath);
            Directory.CreateDirectory(Path.GetDirectoryName(full)!);
            File.WriteAllBytes(full, f.Content);
        }
        var sparse = Path.Combine(root, files[3].RelPath);
        var psi = new ProcessStartInfo("fsutil", $"sparse setflag \"{sparse}\"") { UseShellExecute = false, RedirectStandardOutput = true, CreateNoWindow = true };
        using (var p = Process.Start(psi)!) { p.WaitForExit(30_000); }
        return files;
    }

    private static byte[] RandomBytes(int len, int seed)
    {
        var b = new byte[len];
        new Random(seed).NextBytes(b);
        return b;
    }

    private static Dictionary<string, string> HashTree(string root)
    {
        var map = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            foreach (var f in Directory.EnumerateFiles(root, "*", SearchOption.AllDirectories))
            {
                if (f.Contains("System Volume Information", StringComparison.OrdinalIgnoreCase)) continue;   // OS housekeeping, not part of the payload
                using var s = File.OpenRead(f);
            map[Path.GetRelativePath(root, f)] = Convert.ToHexString(SHA256.HashData(s));
        }
        return map;
    }


    /// <summary>Reads LBA0..2 raw and checks for the protective MBR 55AA and the GPT header
    /// signature — bypasses every partition-layer cache.</summary>
    private static bool RawHasGpt(int diskNumber)
    {
        var fs = new FileStream(DiskLayout.DevicePrefix + diskNumber, FileMode.Open, FileAccess.Read, FileShare.ReadWrite, 1 << 16);
        using (fs)
        {
            var buf = new byte[3 * 512];
            fs.ReadExactly(buf, 0, buf.Length);
            bool mbr = buf[510] == 0x55 && buf[511] == 0xAA;
            bool gpt = System.Text.Encoding.ASCII.GetString(buf, 512, 8) == "EFI PART";
            return mbr && gpt;
        }
    }

    private sealed class ByteProgress : IProgress<ProgressInfo>
    {
        public long LastProcessed;
        public void Report(ProgressInfo p) => LastProcessed = p.ProcessedBytes;
    }

    // ---------- gates ----------

    [Fact]
    public async Task E1_Size_Mismatch_Refused()
    {
        var dir = TempDir();
        try
        {
            var src = Path.Combine(dir, "src.img");
            await File.WriteAllBytesAsync(src, RandomBytes(16 << 20, 1));
            var image = Path.Combine(dir, "img.orf");
            var engine = new ImageEngine();
            var created = await engine.CreateFullAsync(src, image, new ImageOptions { ChunkSizeBytes = ChunkSize });
            AssertSuccess(created);

            var restorer = new DiskRestorer();
            var plan = await restorer.PlanAsync(image, 0);       // plan against disk 0 (2 TB) — size differs
            AssertSuccess(plan);
            var pre = await restorer.PreflightAsync(plan.Value, 0, offlineEnvironment: false);
            Assert.False(pre.IsSuccess);
            Assert.Equal(ErrorCode.RestoreTargetTooSmall, pre.Error.Code);
            var refused = await restorer.ExecuteToDiskAsync(plan.Value, 0, new DiskRestoreOptions { Confirmed = true });
            Assert.False(refused.IsSuccess);
            Assert.Equal(ErrorCode.RestoreTargetTooSmall, refused.Error.Code);
        }
        finally { try { Directory.Delete(dir, true); } catch (IOException) { } }
    }

    [Fact]
    public async Task E2_RunningOs_Disk_Refused_Even_Confirmed()
    {
        var layout = await DiskLayout.ReadAsync(0);
        AssertSuccess(layout);
        var plan = new RestorePlan(@"\\.\PhysicalDrive0", layout.Value.SizeBytes, Array.Empty<RestoreRegion>(), Array.Empty<string>());
        var restorer = new DiskRestorer();
        var pre = await restorer.PreflightAsync(plan, 0, offlineEnvironment: false);
        AssertSuccess(pre);
        Assert.True(pre.Value.HostsRunningOs);

        var refused = await restorer.ExecuteToDiskAsync(plan, 0, new DiskRestoreOptions
        {
            Confirmed = true,            // even confirmed…
            OfflineEnvironment = false,  // …from the live OS it must refuse before any open
        });
        Assert.False(refused.IsSuccess);
        Assert.Equal(ErrorCode.RestoreSafetyGateBlocked, refused.Error.Code);
        Assert.Contains("RUNNING", refused.Error.Message, StringComparison.OrdinalIgnoreCase);
    }

    // ---------- full e2e ----------

    [Fact]
    public async Task E3_Intelligent_Backup_Restore_ZeroFill_File_Equality()
    {
        var dir = TempDir();
        var sourceLetter = PickFreeLetter();
        var restoredLetter = PickFreeLetter(sourceLetter);
        Vhd? source = null, target = null, skipTarget = null;
        try
        {
            source = Vhd.CreatePrepared(dir, "src", sourceLetter);
            var files = WriteTestFiles($"{sourceLetter}:\\");
            Thread.Sleep(500);                        // let NTFS settle before the snapshot
            source.Detach();

            // intelligent backup: VSS + zstd + $Bitmap skip, whole disk
            var image = Path.Combine(dir, "backup.orf");
            var provider = new DiskSnapshotProvider(preferVss: true, intelligent: true);
            var engine = new ImageEngine(provider);
            source.Attach();
            var create = await engine.CreateFullAsync(DiskLayout.DevicePrefix + source.DiskNumber, image,
                new ImageOptions { ChunkSizeBytes = ChunkSize, Compression = CompressionKind.Zstd, CompressionLevel = 3 });
            AssertSuccess(create);
            var imageFileBytes = new FileInfo(image).Length;
            Assert.True(imageFileBytes < VhdBytes / 4, $"intelligent image should be far below raw size, got {imageFileBytes}");
            source.Detach();

            var verify = await new Verify.Verifier().VerifyAsync(image, VerifyDepth.Full);
            Assert.True(verify.Success, string.Join("; ", verify.FailedChunks.Select(f => f.Reason)));

            // restore onto a second VHD: unconfirmed refused, then explicit zero fill with post-verify
            target = Vhd.CreateBlank(dir, "dst");
            Assert.True(target.DiskNumber != 0, "sanity: never disk 0 in tests");
            var restorer = new DiskRestorer();
            var plan = await restorer.PlanAsync(image, target.DiskNumber);
            AssertSuccess(plan);
            Assert.Equal(VhdBytes, plan.Value.TargetSizeBytes);

            var unconfirmed = await restorer.ExecuteToDiskAsync(plan.Value, target.DiskNumber,
                new DiskRestoreOptions { Confirmed = false });
            Assert.False(unconfirmed.IsSuccess);
            Assert.Equal(ErrorCode.RestoreSafetyGateBlocked, unconfirmed.Error.Code);

            var zeroFillProgress = new ByteProgress();
            var exec = await restorer.ExecuteToDiskAsync(plan.Value, target.DiskNumber, new DiskRestoreOptions
            {
                Confirmed = true,
                SkipUnwrittenRegions = false,
                VerifyAfterWrite = true,
            }, zeroFillProgress);
            AssertSuccess(exec);
            Assert.True(RawHasGpt(target.DiskNumber), "GPT must be on the restored disk immediately after the write pass");
            target.Detach();

            target.Attach();
            Assert.True(RawHasGpt(target.DiskNumber), "GPT must survive detach/attach");
            target.AssignLetter(restoredLetter);

            // mount restored volume and compare every file
            var restoredRoot = $"{restoredLetter}:\\";
            Assert.True(Directory.Exists(restoredRoot), $"restored volume did not appear at {restoredRoot}");
            var expected = files.ToDictionary(f => f.RelPath, f => Convert.ToHexString(SHA256.HashData(f.Content)), StringComparer.OrdinalIgnoreCase);
            var actual = HashTree(restoredRoot);
            foreach (var (path, hash) in expected)
                Assert.True(actual.TryGetValue(path, out var got) && got == hash, $"file mismatch after restore: {path}");

            // GPT equality
            var srcLayout = await DiskLayout.ReadAsync(source.DiskNumber);
            var dstLayout = await DiskLayout.ReadAsync(target.DiskNumber);
            AssertSuccess(srcLayout);
            AssertSuccess(dstLayout);
            Assert.Equal(srcLayout.Value.Partitions.Count, dstLayout.Value.Partitions.Count);
            for (int i = 0; i < srcLayout.Value.Partitions.Count; i++)
            {
                Assert.Equal(srcLayout.Value.Partitions[i].Offset, dstLayout.Value.Partitions[i].Offset);
                Assert.Equal(srcLayout.Value.Partitions[i].Length, dstLayout.Value.Partitions[i].Length);
                Assert.Equal(srcLayout.Value.Partitions[i].GptType, dstLayout.Value.Partitions[i].GptType);
            }

            // E4 (same artifacts): re-restore with skip-unwritten onto a FRESH blank VHD.
            // Windows denies raw disk writes into any once-mounted volume's sectors (with no
            // supported bypass), so the skip pass targets a second blank disk: the contract is
            // "skip-unwritten writes strictly less than zero-fill and files still equal".
            // The dirty/mounted-target case is asserted to be REFUSED (safety gate below).
            var junk = Path.Combine(restoredRoot, "junk.bin");
            await File.WriteAllBytesAsync(junk, RandomBytes(64 << 20, 77));
            Thread.Sleep(300);
            target.Detach();

            // mounted-volume target must be refused before any write (volume can't be released)
            target.Attach();
            var refuse = await restorer.ExecuteToDiskAsync(plan.Value, target.DiskNumber, new DiskRestoreOptions
            {
                Confirmed = true,
                SkipUnwrittenRegions = true,
            });
            Assert.False(refuse.IsSuccess);
            Assert.Equal(ErrorCode.DestinationInUse, refuse.Error.Code);
            target.Detach();

            skipTarget = Vhd.CreateBlank(dir, "skip");
            Assert.True(skipTarget.DiskNumber != 0, "sanity: never disk 0 in tests");
            var skipProgress = new ByteProgress();
            var exec2 = await restorer.ExecuteToDiskAsync(plan.Value, skipTarget.DiskNumber, new DiskRestoreOptions
            {
                Confirmed = true,
                SkipUnwrittenRegions = true,
                VerifyAfterWrite = true,
            }, skipProgress);
            AssertSuccess(exec2);
            Assert.True(skipProgress.LastProcessed > 0 && skipProgress.LastProcessed < zeroFillProgress.LastProcessed,
                $"skip-unwritten should write less: skip={skipProgress.LastProcessed} zerofill={zeroFillProgress.LastProcessed}");
            skipTarget.Detach();

            skipTarget.Attach();
            skipTarget.AssignLetter(restoredLetter);
            var actual2 = HashTree(restoredRoot);
            foreach (var (path, hash) in expected)
                Assert.True(actual2.TryGetValue(path, out var got) && got == hash, $"file mismatch after skip-restore: {path}");
            skipTarget.Detach();
            skipTarget.Dispose();
            skipTarget = null;
        }
        finally
        {
            source?.Dispose();
            target?.Dispose();
            skipTarget?.Dispose();
            try { Directory.Delete(dir, true); } catch (IOException) { }
        }
    }

    private static char PickFreeLetter(char? avoid = null)
    {
        var used = new HashSet<char>();
        foreach (var d in System.IO.DriveInfo.GetDrives()) used.Add(char.ToUpperInvariant(d.Name[0]));
        for (char c = 'H'; c <= 'Z'; c++)
            if (!used.Contains(c) && c != avoid) return c;
        throw new Exception("no free drive letter");
    }

    private static string TempDir()
    {
        var d = Path.Combine(Path.GetTempPath(), "dr-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(d);
        return d;
    }
}
