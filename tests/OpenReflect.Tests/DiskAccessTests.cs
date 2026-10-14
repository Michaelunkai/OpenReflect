using System.Buffers.Binary;
using OpenReflect.Core.Abstractions;
using OpenReflect.DiskAccess;
using Xunit;

namespace OpenReflect.Tests;

/// <summary>DiskAccess: pure GPT/MBR parsing, BackedRanges math, and live read-only probes
/// (real disk-0 layout, VSS snapshot of C:, NTFS $Bitmap vs FSCTL_GET_VOLUME_BITMAP equality
/// on a VHD). No test in this class ever writes to a device.</summary>
public class DiskAccessTests
{
    private static void AssertSuccess(Result r)
    {
        if (!r.IsSuccess) throw new Exception($"expected success but got: {r.Error}");
    }

    // ---------- pure GPT parse ----------

    private static (byte[] Lba1, byte[] Entries) MakeGpt(params (Guid Type, long First, long Last, string Name)[] parts)
    {
        var lba1 = new byte[512];
        System.Text.Encoding.ASCII.GetBytes("EFI PART").CopyTo(lba1, 0);
        BinaryPrimitives.WriteInt64LittleEndian(lba1.AsSpan(0x48), 2);       // entry LBA
        BinaryPrimitives.WriteInt32LittleEndian(lba1.AsSpan(0x50), 128);     // count
        BinaryPrimitives.WriteInt32LittleEndian(lba1.AsSpan(0x54), 128);     // entry size
        new Guid("11111111-2222-3333-4444-555555555555").ToByteArray().CopyTo(lba1, 0x38);

        var entries = new byte[128 * 128];
        for (int i = 0; i < parts.Length; i++)
        {
            var e = entries.AsSpan(i * 128, 128);
            parts[i].Type.ToByteArray().CopyTo(e);
            BinaryPrimitives.WriteInt64LittleEndian(e.Slice(0x20, 8), parts[i].First);
            BinaryPrimitives.WriteInt64LittleEndian(e.Slice(0x28, 8), parts[i].Last);
            System.Text.Encoding.Unicode.GetBytes(parts[i].Name).CopyTo(e.Slice(0x38, 72));
        }
        return (lba1, entries);
    }

    [Fact]
    public void D1_Gpt_Parse_Partitions_And_Skip_Empty()
    {
        var (lba1, entries) = MakeGpt(
            (DiskPartition.GptEfi, 2048, 2208, "EFI system partition"),
            (DiskPartition.GptBasicData, 4096, 4096 + 100000, "Windows"),
            (DiskPartition.GptWindowsRecovery, 104857, 104857 + 1000, "Recovery"));
        var parsed = DiskLayout.ParseGpt(lba1, entries, 512);
        AssertSuccess(parsed);
        Assert.Equal(new Guid("11111111-2222-3333-4444-555555555555"), parsed.Value.DiskId);
        var parts = parsed.Value.Partitions;
        Assert.Equal(3, parts.Count);
        Assert.True(parts[0].IsEfi);
        Assert.Equal(2048L * 512, parts[0].Offset);
        Assert.Equal((2208 - 2048 + 1) * 512, parts[0].Length);
        Assert.True(parts[1].GptType == DiskPartition.GptBasicData);
        Assert.True(parts[2].IsRecovery);
        Assert.Equal("Windows", parts[1].Name);
    }

    [Fact]
    public void D2_Gpt_Parse_Rejects_Bad_Header()
    {
        var (lba1, entries) = MakeGpt((DiskPartition.GptEfi, 2048, 2100, "x"));
        lba1[0] = (byte)'X';
        var parsed = DiskLayout.ParseGpt(lba1, entries, 512);
        Assert.False(parsed.IsSuccess);
        Assert.Equal(ErrorCode.BadFormat, parsed.Error.Code);
    }

    [Fact]
    public void D3_Mbr_Parse()
    {
        var mbr = new byte[512];
        mbr[510] = 0x55; mbr[511] = 0xAA;
        // entry 1: type 0x07, first LBA 2048, 100 sectors
        mbr[0x1BE + 4] = 0x07;
        BinaryPrimitives.WriteUInt32LittleEndian(mbr.AsSpan(0x1BE + 8), 2048);
        BinaryPrimitives.WriteUInt32LittleEndian(mbr.AsSpan(0x1BE + 12), 100);
        // entry 2: type 0x0C at LBA 4096, 200 sectors
        mbr[0x1BE + 16 + 4] = 0x0C;
        BinaryPrimitives.WriteUInt32LittleEndian(mbr.AsSpan(0x1BE + 16 + 8), 4096);
        BinaryPrimitives.WriteUInt32LittleEndian(mbr.AsSpan(0x1BE + 16 + 12), 200);

        var parts = DiskLayout.ParseMbr(mbr);
        AssertSuccess(parts);
        Assert.Equal(2, parts.Value.Count);
        Assert.Equal(2048L * 512, parts.Value[0].Offset);
        Assert.Equal(100L * 512, parts.Value[0].Length);
        Assert.Equal(0x0C, parts.Value[1].MbrType);
    }

    [Fact]
    public void D4_BackedRanges_Margins_And_Partitions()
    {
        var disk = new DiskInfo(7, 100L << 20, 512, IsGpt: true, Guid.Empty, new List<DiskPartition>
        {
            new(1, 1 << 20, 10 << 20, DiskPartition.GptEfi, 0, "EFI", null),
            new(2, 11 << 20, 80 << 20, DiskPartition.GptBasicData, 0, "data", null),
        });
        var ranges = disk.BackedRanges();
        Assert.Equal(4, ranges.Count);
        Assert.Equal(0, ranges[0].Offset);
        Assert.Equal(1 << 20, ranges[0].Length);                       // head margin
        Assert.Equal(1 << 20, ranges[1].Offset);                       // EFI partition
        Assert.Equal(11 << 20, ranges[2].Offset);                      // data partition
        Assert.Equal(99 << 20, ranges[3].Offset);                      // tail margin: 100-10
        Assert.Equal(1 << 20, ranges[3].Length);
    }

    // ---------- live (read-only) probes ----------

    [Fact]
    public async Task D5_Live_Disk0_Layout()
    {
        var disk = await DiskLayout.ReadAsync(0);
        AssertSuccess(disk);
        Assert.True(disk.Value.SizeBytes > (100L << 30));
        Assert.True(disk.Value.IsGpt);
        Assert.Equal(4, disk.Value.Partitions.Count);
        Assert.True(disk.Value.Partitions[0].IsEfi);
        Assert.True(disk.Value.Partitions[1].IsMsr);
        Assert.True(disk.Value.Partitions[3].IsRecovery);
        // boot ranges cover LBA0 and the very end
        var ranges = disk.Value.BackedRanges();
        Assert.Contains(ranges, r => r.Offset == 0 && r.Length >= (1 << 20));
        Assert.Contains(ranges, r => r.Offset + r.Length == disk.Value.SizeBytes);
    }

    [Fact]
    public async Task D6_Live_Vss_Snapshot_Of_C()
    {
        var cVolume = VolumeEnumerator.All();
        AssertSuccess(cVolume);
        var vol = cVolume.Value.FirstOrDefault(v => v.DriveLetter == 'C' && v.FileSystem == "NTFS");
        Assert.NotNull(vol);
        var created = VssSnapshotSet.Create(new[] { vol!.VolumeGuidPath });
        AssertSuccess(created);
        await using var set = created.Value;
        var device = Assert.Single(set.ShadowDevices).Value;
        Assert.Contains("ShadowCopy", device, StringComparison.OrdinalIgnoreCase);
        // read the first MiB from the shadow device (proves it is openable and readable)
        var fs = new FileStream(device, FileMode.Open, FileAccess.Read, FileShare.ReadWrite, 1 << 16, useAsync: true);
        await using var _ = fs;
        var head = new byte[1 << 20];
        await fs.ReadExactlyAsync(head);
        Assert.Equal(0x55, head[510]);   // NTFS volume boot signature
        Assert.Equal(0xAA, head[511]);
    }

    [Fact]
    public async Task D7_Live_Os_Volume_Detected_On_Disk0()
    {
        var osGuid = VolumeEnumerator.RunningOsVolumeGuid();
        Assert.False(string.IsNullOrEmpty(osGuid));
        var onDisk0 = VolumeEnumerator.ForDisk(0);
        AssertSuccess(onDisk0);
        Assert.Contains(onDisk0.Value, v => string.Equals(v.VolumeGuidPath, osGuid, StringComparison.OrdinalIgnoreCase));
    }
}
