using System.Buffers.Binary;
using OpenReflect.Core.Abstractions;

namespace OpenReflect.DiskAccess;

/// <summary>Locates the cluster extents of pagefile.sys / hiberfil.sys / swapfile.sys on an NTFS
/// volume by walking $MFT file records on the SAME stream the backup reads (the VSS shadow), so
/// exclusion decisions are as crash-consistent as the $Bitmap parse. Those files' contents are
/// meaningless on restore (Windows recreates them), so the imager may skip them entirely and let
/// the restored sectors stay zeros — Macrium exclusion parity, minus the tens of GB of
/// nearly-incompressible bytes they add to the image. Best-effort by design: any parse trouble
/// returns whatever was found so far (possibly nothing) and the backup proceeds with NO exclusion
/// (never a failed backup).</summary>
public static class MftFileLocator
{
    /// <summary>Root-directory files whose extents may be excluded from images.</summary>
    public static readonly string[] ExcludedNames = { "pagefile.sys", "hiberfil.sys", "swapfile.sys" };

    /// <summary>Volume-coordinate byte ranges covered by the named files' unnamed $DATA runs.
    /// Sparse runs contribute nothing (they are already zeros). Scans at most maxRecords MFT
    /// records and stops early once every name has been found.</summary>
    public static async Task<List<BackingRange>> LocateAsync(Stream device, NtfsAllocation ntfs,
        int maxRecords = 2_000_000, CancellationToken ct = default)
    {
        var found = new List<BackingRange>();
        try
        {
            long vcnBytes = ntfs.BytesPerSector * ntfs.SectorsPerCluster;
            long recSize = ntfs.MftRecordSize;

            // $MFT's own runlist (record 0) locates every file record; $MFT may be fragmented,
            // so records are walked per positive run (a sparse run holds no records)
            byte[] mftRec = new byte[recSize];
            device.Seek(ntfs.MftLcn * vcnBytes, SeekOrigin.Begin);
            await device.ReadExactlyAsync(mftRec, ct).ConfigureAwait(false);
            if (System.Text.Encoding.ASCII.GetString(mftRec, 0, 4) != "FILE") return found;
            NtfsAllocation.ApplyFixup(mftRec);
            var mftRuns = NtfsAllocation.ParseDataRuns(mftRec);
            if (mftRuns is null || mftRuns.Count == 0) return found;

            var pending = new HashSet<string>(ExcludedNames, StringComparer.OrdinalIgnoreCase);
            long recordsLeft = maxRecords;
            foreach (var (lcn, clusters) in mftRuns)
            {
                if (lcn < 0) continue;
                long runBytes = clusters * vcnBytes;
                // walk the run in record-aligned <=4 MiB pieces (runs are cluster-aligned, so
                // pieces stay sector-aligned — safe for shadow devices)
                for (long pieceStart = 0; pieceStart < runBytes && recordsLeft > 0; pieceStart += pieceSize(runBytes, pieceStart, recSize))
                {
                    int piece = pieceSize(runBytes, pieceStart, recSize);
                    var buf = new byte[piece];
                    device.Seek(lcn * vcnBytes + pieceStart, SeekOrigin.Begin);
                    await device.ReadExactlyAsync(buf, ct).ConfigureAwait(false);
                    for (int off = 0; off + recSize <= piece && recordsLeft > 0; off += (int)recSize, recordsLeft--)
                    {
                        if (System.Text.Encoding.ASCII.GetString(buf, off, 4) != "FILE") continue;
                        var rec = new byte[recSize];
                        Buffer.BlockCopy(buf, off, rec, 0, (int)recSize);
                        try { NtfsAllocation.ApplyFixup(rec); }
                        catch (InvalidDataException) { continue; }   // stale/garbage record — never fatal
                        if (TryMatchExcluded(rec, pending, vcnBytes, found, out var matched))
                            foreach (var name in matched) pending.Remove(name);
                    }
                }
                if (pending.Count == 0) break;
            }
        }
        catch
        {
            // exclusion is an optimization: on ANY trouble return what we have (possibly nothing)
        }
        return found;
    }

    private static int pieceSize(long runBytes, long pieceStart, long recSize)
    {
        long piece = Math.Min(runBytes - pieceStart, 4L << 20);
        piece -= piece % recSize;
        return (int)piece;
    }

    /// <summary>True when the record is a root-directory file whose name is on the exclusion list;
    /// appends its non-sparse $DATA extents (volume bytes) to <paramref name="ranges"/> and reports
    /// which pending names matched. Parent must be record 5 (root) so a stray
    /// Documents\pagefile.sys can never be silently zeroed in an image.</summary>
    private static bool TryMatchExcluded(byte[] record, HashSet<string> pending, long clusterBytes,
        List<BackingRange> ranges, out List<string> matched)
    {
        matched = new List<string>();
        string? name = null;
        bool parentIsRoot = false;

        ushort attrOffset = BinaryPrimitives.ReadUInt16LittleEndian(record.AsSpan(0x14));
        int recLen = BinaryPrimitives.ReadInt32LittleEndian(record.AsSpan(0x1C));
        if (attrOffset <= 0 || attrOffset >= record.Length) return false;
        int off = attrOffset;
        List<(long Lcn, long Clusters)>? data = null;
        while (off + 16 <= record.Length && off < recLen)
        {
            uint type = BinaryPrimitives.ReadUInt32LittleEndian(record.AsSpan(off));
            int len = BinaryPrimitives.ReadInt32LittleEndian(record.AsSpan(off + 4));
            if (type == 0xFFFFFFFF || len <= 0) break;
            bool nonResident = record[off + 8] != 0;
            byte nameLen = record[off + 9];
            // NOTE: nameLen here is the ATTRIBUTE name length — $FILE_NAME attrs carry none
            // (it is always 0); the FILE name lives inside the attribute value.
            if (type == 0x30 && !nonResident && nameLen == 0 && name is null)
            {
                ushort voff = BinaryPrimitives.ReadUInt16LittleEndian(record.AsSpan(off + 0x14));
                int vlen = BinaryPrimitives.ReadInt32LittleEndian(record.AsSpan(off + 0x10));
                if (voff > 0 && vlen >= 0x44 && off + voff + vlen <= record.Length)
                {
                    parentIsRoot = (BinaryPrimitives.ReadUInt64LittleEndian(record.AsSpan(off + voff)) & 0xFFFFFFFFFFFF) == 5;
                    byte fnLen = record[off + voff + 0x40];
                    if (fnLen > 0 && 0x42 + fnLen * 2 <= vlen)
                        name = System.Text.Encoding.Unicode.GetString(record, off + voff + 0x42, fnLen * 2);
                }
            }
            else if (type == 0x80 && nonResident && nameLen == 0 && data is null)
            {
                ushort runsOffset = BinaryPrimitives.ReadUInt16LittleEndian(record.AsSpan(off + 0x20));
                long realSize = BinaryPrimitives.ReadInt64LittleEndian(record.AsSpan(off + 0x30));
                data = NtfsAllocation.ParseRunlist(record, off + runsOffset);
                if (data is not null && realSize >= 0) data = ClipToSize(data, realSize, clusterBytes);
            }
            off += len;
        }

        if (name is null || !parentIsRoot) return false;
        bool hit = pending.Contains(name);
        if (!hit || data is null) return false;
        matched.Add(name);
        foreach (var (lcn, clusters) in data)
            if (lcn >= 0 && clusters > 0)
                ranges.Add(new BackingRange(lcn * clusterBytes, clusters * clusterBytes));
        return true;
    }

    /// <summary>Truncates a runlist to the file's real $DATA size so stale clusters past the end
    /// (leftover from a larger previous allocation) never enter an exclusion range.</summary>
    private static List<(long Lcn, long Clusters)> ClipToSize(List<(long Lcn, long Clusters)> runs, long sizeBytes, long clusterBytes)
    {
        var clipped = new List<(long, long)>();
        long left = sizeBytes;
        foreach (var (lcn, clusters) in runs)
        {
            if (left <= 0) break;
            long take = Math.Min(clusters * clusterBytes, left);
            clipped.Add((lcn, (take + clusterBytes - 1) / clusterBytes));
            left -= clusters * clusterBytes;
        }
        return clipped;
    }
}
