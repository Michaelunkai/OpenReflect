using System.Buffers.Binary;
using OpenReflect.Core.Abstractions;

namespace OpenReflect.DiskAccess;

/// <summary>Allocation state of one NTFS volume, parsed from the volume's OWN boot sector + MFT
/// record 6 ($Bitmap) + USA fixup. Used so intelligent-sector skipping reads allocation truth from
/// the SAME stream as the data (a VSS shadow device), never from the live volume.</summary>
public sealed class NtfsAllocation
{
    public required uint BytesPerSector { get; init; }
    public required uint SectorsPerCluster { get; init; }
    public required long TotalClusters { get; init; }
    public required byte[] Bitmap { get; init; }

    public uint BytesPerCluster => BytesPerSector * SectorsPerCluster;
    public bool IsAllocated(long cluster) => cluster >= 0 && cluster < TotalClusters &&
        (Bitmap[(int)(cluster >> 3)] & (1 << (int)(cluster & 7))) != 0;

    /// <summary>Allocated byte ranges in VOLUME coordinates (offset from volume start).
    /// Runs shorter than <paramref name="gapToleranceBytes"/> apart are merged so a chunk that
    /// straddles a tiny free gap is still read.</summary>
    public List<BackingRange> AllocatedRanges(long gapToleranceBytes)
    {
        var list = new List<BackingRange>();
        long clusterBytes = BytesPerCluster;
        long runStart = -1;
        long lastAllocatedEnd = -1;
        for (long c = 0; c <= TotalClusters; c++)
        {
            bool allocated = c < TotalClusters && IsAllocated(c);
            if (allocated && runStart < 0) runStart = c;
            if ((!allocated || c == TotalClusters) && runStart >= 0)
            {
                long end = c; // exclusive cluster
                list.Add(new BackingRange(runStart * clusterBytes, (end - runStart) * clusterBytes));
                lastAllocatedEnd = end * clusterBytes;
                runStart = -1;
            }
        }
        // merge with tolerance
        list.Sort((a, b) => a.Offset.CompareTo(b.Offset));
        var merged = new List<BackingRange>();
        foreach (var r in list)
        {
            if (merged.Count > 0 && r.Offset - (merged[^1].Offset + merged[^1].Length) <= gapToleranceBytes)
            {
                var last = merged[^1];
                merged[^1] = new BackingRange(last.Offset, Math.Max(last.Offset + last.Length, r.Offset + r.Length) - last.Offset);
            }
            else merged.Add(r);
        }
        return merged;
    }

    /// <summary>Parses NTFS allocation from the first sectors + MFT of a readable device stream.
    /// The stream must be positioned anywhere; seeks are used freely.</summary>
    public static async Task<Result<NtfsAllocation>> ReadAsync(Stream device, long deviceLength, CancellationToken ct = default)
    {
        var boot = new byte[512];
        try
        {
            device.Seek(0, SeekOrigin.Begin);
            await device.ReadExactlyAsync(boot, ct).ConfigureAwait(false);
        }
        catch (IOException ex) { return Error.Of(ErrorCode.SourceReadError, "cannot read NTFS boot sector", ex.Message); }
        if (boot[510] != 0x55 || boot[511] != 0xAA)
            return Error.Of(ErrorCode.BadFormat, "no 55AA signature — not a bootable NTFS volume");
        if (!System.Text.Encoding.ASCII.GetString(boot, 3, 8).StartsWith("NTFS"))
            return Error.Of(ErrorCode.BadFormat, "OEM signature is not NTFS");

        uint bytesPerSector = BinaryPrimitives.ReadUInt16LittleEndian(boot.AsSpan(0x0B));
        uint sectorsPerCluster = boot[0x0D];
        long totalSectors = BinaryPrimitives.ReadInt64LittleEndian(boot.AsSpan(0x28));
        long mftLcn = BinaryPrimitives.ReadInt64LittleEndian(boot.AsSpan(0x30));
        sbyte clustersPerMft = (sbyte)boot[0x40];
        if (bytesPerSector < 512 || sectorsPerCluster == 0 || totalSectors <= 0 || mftLcn < 0 || clustersPerMft == 0)
            return Error.Of(ErrorCode.BadFormat, "implausible NTFS boot sector fields");

        long mftRecordSize = clustersPerMft > 0 ? clustersPerMft * bytesPerSector * sectorsPerCluster : 1L << -clustersPerMft;
        if (mftRecordSize is < 1024 or > 1 << 20) return Error.Of(ErrorCode.BadFormat, $"implausible MFT record size {mftRecordSize}");
        long totalClusters = totalSectors / sectorsPerCluster;

        var record = new byte[mftRecordSize];
        try
        {
            device.Seek((mftLcn * sectorsPerCluster * bytesPerSector) + 6 * mftRecordSize, SeekOrigin.Begin);
            await device.ReadExactlyAsync(record, ct).ConfigureAwait(false);
        }
        catch (IOException ex) { return Error.Of(ErrorCode.SourceReadError, "cannot read MFT record 6 ($Bitmap)", ex.Message); }
        if (System.Text.Encoding.ASCII.GetString(record, 0, 4) != "FILE")
            return Error.Of(ErrorCode.BadFormat, "MFT record 6 lacks FILE magic");

        ApplyFixup(record);

        var runs = ParseDataRuns(record);
        if (runs is null || runs.Count == 0) return Error.Of(ErrorCode.BadFormat, "no non-resident $DATA runlist on $Bitmap");

        long bitmapBytes = (totalClusters + 7) / 8;
        var bitmap = new byte[bitmapBytes];
        long vcnBytes = bytesPerSector * sectorsPerCluster;
        long filled = 0;
        foreach (var (lcn, clusters) in runs)
        {
            if (lcn < 0) continue;   // sparse run in $Bitmap (shouldn't exist) — leaves zeros
            long want = Math.Min(clusters * vcnBytes, bitmapBytes - filled);
            if (want <= 0) break;
            int done = 0;
            try
            {
                device.Seek(lcn * vcnBytes, SeekOrigin.Begin);
                // shadow devices reject very large and sector-unaligned reads — pull in
                // sector-aligned <=4 MiB pieces; the sub-sector tail goes through a 512 buffer
                while (done < want)
                {
                    int remaining = (int)(want - done);
                    int piece = Math.Min(remaining, 4 << 20) & ~511;
                    if (piece >= 512)
                    {
                        await device.ReadExactlyAsync(bitmap.AsMemory((int)filled + done, piece), ct).ConfigureAwait(false);
                        done += piece;
                        continue;
                    }
                    var tail = new byte[512];
                    await device.ReadExactlyAsync(tail, ct).ConfigureAwait(false);
                    tail.AsSpan(0, remaining).CopyTo(bitmap.AsSpan((int)filled + done, remaining));
                    done += remaining;
                }
            }
            catch (IOException ex) { return Error.Of(ErrorCode.SourceReadError, $"cannot read $Bitmap data at lcn {lcn} (offset {lcn * vcnBytes}, want {want}, done {done}, canRead={device.CanRead} async={device is System.IO.FileStream fs && fs.IsAsync}): {ex.Message}"); }
            filled += want;
        }
        if (filled < bitmapBytes) return Error.Of(ErrorCode.BadFormat, $"$Bitmap runlist covered {filled} of {bitmapBytes} bytes");

        return new NtfsAllocation
        {
            BytesPerSector = bytesPerSector,
            SectorsPerCluster = sectorsPerCluster,
            TotalClusters = totalClusters,
            Bitmap = bitmap,
        };
    }

    /// <summary>Multi-sector-header fixup: the last 2 bytes of each 512-byte sector in the record
    /// hold the update-sequence number; the real values sit in the USA array at 0x04.</summary>
    private static void ApplyFixup(byte[] record)
    {
        ushort usaOffset = BinaryPrimitives.ReadUInt16LittleEndian(record.AsSpan(0x04));
        ushort usaCount = BinaryPrimitives.ReadUInt16LittleEndian(record.AsSpan(0x06));
        if (usaOffset == 0 || usaCount == 0 || usaOffset + usaCount * 2 > record.Length) return;
        ushort signature = BinaryPrimitives.ReadUInt16LittleEndian(record.AsSpan(usaOffset));
        for (int i = 1; i < usaCount; i++)
        {
            int sectorEnd = i * 512 - 2;
            if (sectorEnd + 2 > record.Length) break;
            ushort actual = BinaryPrimitives.ReadUInt16LittleEndian(record.AsSpan(sectorEnd));
            if (actual != signature) throw new InvalidDataException($"USA mismatch in MFT record at sector {i}");
            ushort correct = BinaryPrimitives.ReadUInt16LittleEndian(record.AsSpan(usaOffset + i * 2));
            BinaryPrimitives.WriteUInt16LittleEndian(record.AsSpan(sectorEnd), correct);
        }
    }

    /// <summary>Finds the unnamed $DATA attribute and parses its non-resident runlist into
    /// (lcn, clusterCount) runs. Negative lcn = sparse.</summary>
    private static List<(long Lcn, long Clusters)>? ParseDataRuns(byte[] record)
    {
        ushort attrOffset = BinaryPrimitives.ReadUInt16LittleEndian(record.AsSpan(0x14));
        int recLen = BinaryPrimitives.ReadInt32LittleEndian(record.AsSpan(0x1C));
        if (attrOffset <= 0 || attrOffset >= record.Length) return null;
        int off = attrOffset;
        List<(long, long)>? unnamedData = null;
        while (off + 16 <= record.Length && off < recLen)
        {
            uint type = BinaryPrimitives.ReadUInt32LittleEndian(record.AsSpan(off));
            int len = BinaryPrimitives.ReadInt32LittleEndian(record.AsSpan(off + 4));
            if (type == 0xFFFFFFFF || len <= 0) break;
            bool nonResident = record[off + 8] != 0;
            byte nameLen = record[off + 9];
            if (type == 0x80 && nonResident && nameLen == 0)
            {
                ushort runsOffset = BinaryPrimitives.ReadUInt16LittleEndian(record.AsSpan(off + 0x20));
                unnamedData = ParseRunlist(record, off + runsOffset);
                break;   // record 6 has exactly one unnamed $DATA
            }
            off += len;
        }
        return unnamedData;
    }

    private static List<(long Lcn, long Clusters)>? ParseRunlist(byte[] record, int pos)
    {
        var runs = new List<(long, long)>();
        long lcn = 0;
        while (pos < record.Length)
        {
            byte header = record[pos];
            if (header == 0) break;
            int lenBytes = header & 0x0F;
            int offBytes = header >> 4;
            if (lenBytes == 0 || lenBytes > 8 || offBytes > 8 || pos + 1 + lenBytes + offBytes > record.Length) return null;
            long clusters = 0;
            for (int i = 0; i < lenBytes; i++) clusters |= (long)record[pos + 1 + i] << (8 * i);
            pos += 1 + lenBytes;
            long delta = 0;
            bool negative = offBytes > 0 && (record[pos + offBytes - 1] & 0x80) != 0;
            for (int i = 0; i < offBytes; i++) delta |= (long)record[pos + i] << (8 * i);
            pos += offBytes;
            if (negative) delta -= 1L << (8 * offBytes);
            if (clusters == 0) return null;
            lcn += delta;
            runs.Add((lcn, clusters));
        }
        return runs.Count > 0 ? runs : null;
    }
}
