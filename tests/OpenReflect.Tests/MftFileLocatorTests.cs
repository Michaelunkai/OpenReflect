using OpenReflect.Core.Abstractions;
using OpenReflect.DiskAccess;
using System.Buffers.Binary;

namespace OpenReflect.Tests;

/// <summary>Regression: a fragmented $MFT whose run is not a multiple of the MFT record size
/// (e.g. 512-byte clusters, 1024-byte records, 1-cluster run) used to yield a zero-length
/// read piece and spin the scan forever. The locator must skip the partial tail and return.</summary>
public class MftFileLocatorTests
{
    // 512-byte clusters, 1024-byte MFT records → a 1-cluster run leaves 512 bytes, which is
    // floored to 0 by the record alignment — the exact geometry that hung the old loop.
    private const int SectorBytes = 512;
    private const int RecSize = 1024;

    [Fact]
    public async Task LocateAsync_ZeroRecordPiece_TerminatesInsteadOfHanging()
    {
        // run A: 1 cluster (512 B → piece would be 0); run B: 4 clusters (2 full records)
        var device = new MemoryStream(BuildFakeVolume(mftRuns: [(5L, 1L), (6L, 4L)]));
        var ntfs = new NtfsAllocation
        {
            BytesPerSector = SectorBytes,
            SectorsPerCluster = 1,
            TotalClusters = 10_000,
            Bitmap = new byte[1250],
            MftLcn = 0,
            MftRecordSize = RecSize,
        };

        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(20));
        var found = await MftFileLocator.LocateAsync(device, ntfs, maxRecords: 1000, cts.Token);

        Assert.Empty(found);   // run data holds no pagefile/hiberfil/swapfile records
    }

    /// <summary>Builds a volume image whose $MFT record 0 ("FILE" + valid USA + one non-resident
    /// unnamed $DATA) carries the given runs; run data itself is zeros (no "FILE" records).</summary>
    private static byte[] BuildFakeVolume(IReadOnlyList<(long Lcn, long Clusters)> mftRuns)
    {
        long volumeBytes = mftRuns.Max(r => (r.Lcn + r.Clusters) * SectorBytes) + SectorBytes;
        var volume = new byte[volumeBytes];

        // --- $MFT record 0 at LCN 0 ---
        var rec = new byte[RecSize];
        System.Text.Encoding.ASCII.GetBytes("FILE").CopyTo(rec, 0);
        // USA: array at 0x30, count 1 → fixup loop body never runs (nothing to repair)
        BinaryPrimitives.WriteUInt16LittleEndian(rec.AsSpan(0x04), 0x30);
        BinaryPrimitives.WriteUInt16LittleEndian(rec.AsSpan(0x06), 1);
        BinaryPrimitives.WriteUInt16LittleEndian(rec.AsSpan(0x14), 0x40);   // first attribute at 0x40
        BinaryPrimitives.WriteInt32LittleEndian(rec.AsSpan(0x1C), RecSize); // used record length

        // non-resident unnamed $DATA at 0x40; runlist at 0x40 + 0x40 = 0x80
        const int attr = 0x40;
        const int runlistOff = attr + 0x40;
        BinaryPrimitives.WriteUInt32LittleEndian(rec.AsSpan(attr), 0x80);            // $DATA
        BinaryPrimitives.WriteInt32LittleEndian(rec.AsSpan(attr + 4), 0x50);         // attr length
        rec[attr + 8] = 1;                                                           // non-resident
        rec[attr + 9] = 0;                                                           // no name
        BinaryPrimitives.WriteUInt16LittleEndian(rec.AsSpan(attr + 0x20), 0x40);     // runlist offset

        int pos = runlistOff;
        foreach (var (lcn, clusters) in mftRuns)
        {
            rec[pos++] = 0x11;           // 1-byte length for both cluster count and LCN delta
            rec[pos++] = (byte)clusters;
            rec[pos++] = (byte)lcn;      // positive delta (runs are ascending)
        }
        rec[pos] = 0;                    // runlist terminator
        rec.AsSpan(0, RecSize).CopyTo(volume.AsSpan(0));   // $MFT at MftLcn 0

        // run data stays zeros — never matches "FILE", so the scan walks through cleanly
        return volume;
    }
}
