using System.Buffers.Binary;
using OpenReflect.Core.Abstractions;

namespace OpenReflect.DiskAccess;

public sealed record DiskPartition(
    int Number,
    long Offset,
    long Length,
    Guid GptType,
    byte MbrType,
    string Name,
    string? VolumeGuidPath)
{
    public static readonly Guid GptEfi = new("c12a7328-f81f-11d2-ba4b-00a0c93ec93b");
    public static readonly Guid GptMsr = new("e3c9e316-0b5c-4db8-817d-f92df00215ae");
    public static readonly Guid GptBasicData = new("ebd0a0a2-b9e5-4433-87c0-68b6b72699c7");
    public static readonly Guid GptWindowsRecovery = new("de94bba4-06d1-4d40-a16a-bfd50179d6ac");

    public bool IsEfi => GptType == GptEfi;
    public bool IsMsr => GptType == GptMsr;
    public bool IsRecovery => GptType == GptWindowsRecovery;
}

public sealed record DiskInfo(
    int Number,
    long SizeBytes,
    uint SectorSize,
    bool IsGpt,
    Guid GptDiskId,
    IReadOnlyList<DiskPartition> Partitions)
{
    /// <summary>Intelligent-sector backing for the whole disk: protective MBR + primary GPT array
    /// (first margin) + backup GPT (last margin) + every partition extent. Unallocated disk gaps
    /// are excluded — restore treats them as free space (Macrium 'intelligent sector' parity).</summary>
    public IReadOnlyList<BackingRange> BackedRanges(int marginBytes = 1 << 20)
    {
        var list = new List<BackingRange>();
        void Add(long off, long len)
        {
            if (len <= 0) return;
            off = Math.Max(0, off);
            len = Math.Min(len, SizeBytes - off);
            if (len > 0) list.Add(new BackingRange(off, len));
        }
        Add(0, marginBytes);
        foreach (var p in Partitions) Add(p.Offset, p.Length);
        Add(SizeBytes - marginBytes, marginBytes);
        return list;
    }
}

/// <summary>Reads a disk's layout. GPT/MBR parsing is pure byte work (unit-testable); the device
/// path only opens the disk read-only and reads LBA0/1 + the entry array.</summary>
public static class DiskLayout
{
    public const string DevicePrefix = @"\\.\PhysicalDrive";

    public static async Task<Result<DiskInfo>> ReadAsync(int diskNumber, CancellationToken ct = default)
    {
        var path = DevicePrefix + diskNumber;
        var opened = Win32.Open(path, write: false, exclusive: false);
        if (!opened.IsSuccess) return Result<DiskInfo>.Fail(opened.Error);
        using var device = opened.Value;

        // DISK_GEOMETRY_EX: geometry (24 B) + disk size (8 B) + device path. Preferred over
        // GET_LENGTH_INFO, which some storage stacks answer with ERROR_INVALID_FUNCTION.
        long diskSize = 0;
        uint sectorSize = 512;
        var geomEx = Win32.IoControlRaw(device, Win32.IOCTL_DISK_GET_DRIVE_GEOMETRY_EX, 64);
        if (geomEx.IsSuccess && geomEx.Value.Length >= 32)
        {
            sectorSize = BitConverter.ToUInt32(geomEx.Value, 20);
            diskSize = BitConverter.ToInt64(geomEx.Value, 24);
        }
        else
        {
            var length = Win32.IoControl<Win32.GET_LENGTH_INFORMATION>(device, Win32.IOCTL_DISK_GET_LENGTH_INFO, sizeof(long));
            if (!length.IsSuccess) return Result<DiskInfo>.Fail(length.Error);
            diskSize = length.Value.Length;
            var geom = Win32.IoControl<Win32.DISK_GEOMETRY>(device, Win32.IOCTL_DISK_GET_DRIVE_GEOMETRY,
                System.Runtime.InteropServices.Marshal.SizeOf<Win32.DISK_GEOMETRY>());
            if (geom.IsSuccess) sectorSize = geom.Value.BytesPerSector;
        }
        if (diskSize <= 0) return Error.Of(ErrorCode.SourceReadError, $"disk {diskNumber} reports size {diskSize}");
        if (sectorSize < 512 || (sectorSize & (sectorSize - 1)) != 0 || sectorSize > 4096) sectorSize = 512;

        byte[] head;
        try
        {
            var fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite, 1 << 16, useAsync: true);
            await using (var _ = fs)
            {
                head = new byte[2 * sectorSize];
                await fs.ReadExactlyAsync(head, ct).ConfigureAwait(false);
            }
        }
        catch (IOException ex)
        {
            return Error.Of(ErrorCode.SourceReadError, $"cannot read {path} first sectors", ex.Message);
        }

        byte[] lba0 = head.AsSpan(0, (int)sectorSize).ToArray();
        byte[] lba1 = head.AsSpan((int)sectorSize, (int)sectorSize).ToArray();

        if (System.Text.Encoding.ASCII.GetString(lba1, 0, 8) == "EFI PART")
        {
            long entryLba = BinaryPrimitives.ReadInt64LittleEndian(lba1.AsSpan(0x48));
            int entryCount = BinaryPrimitives.ReadInt32LittleEndian(lba1.AsSpan(0x50));
            int entrySize = BinaryPrimitives.ReadInt32LittleEndian(lba1.AsSpan(0x54));
            if (entryLba <= 0 || entryCount is <= 0 or > 1024 || entrySize is < 64 or > 1024)
                return Error.Of(ErrorCode.BadFormat, $"implausible GPT header: entryLba={entryLba} count={entryCount} size={entrySize}");

            byte[] entries;
            try
            {
                var fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite, 1 << 16, useAsync: true);
                await using (var _ = fs)
                {
                    entries = new byte[entryCount * entrySize];
                    fs.Seek(entryLba * sectorSize, SeekOrigin.Begin);
                    await fs.ReadExactlyAsync(entries, ct).ConfigureAwait(false);
                }
            }
            catch (IOException ex)
            {
                return Error.Of(ErrorCode.SourceReadError, $"cannot read {path} GPT entry array", ex.Message);
            }
            var parsed = ParseGpt(lba1, entries, sectorSize);
            if (!parsed.IsSuccess) return Result<DiskInfo>.Fail(parsed.Error);
            return new DiskInfo(diskNumber, diskSize, sectorSize, IsGpt: true, parsed.Value.DiskId, parsed.Value.Partitions);
        }

        var mbr = ParseMbr(lba0);
        if (!mbr.IsSuccess) return Result<DiskInfo>.Fail(mbr.Error);
        return new DiskInfo(diskNumber, diskSize, sectorSize, IsGpt: false, Guid.Empty, mbr.Value);
    }

    /// <summary>Pure GPT parse: header LBA + entry array → partitions. Testable without any device.</summary>
    public static Result<(Guid DiskId, List<DiskPartition> Partitions)> ParseGpt(byte[] lba1, byte[] entries, uint sectorSize)
    {
        if (lba1.Length < 0x60 || System.Text.Encoding.ASCII.GetString(lba1, 0, 8) != "EFI PART")
            return Error.Of(ErrorCode.BadFormat, "not a GPT header (EFI PART signature missing)");
        int entryCount = BinaryPrimitives.ReadInt32LittleEndian(lba1.AsSpan(0x50));
        int entrySize = BinaryPrimitives.ReadInt32LittleEndian(lba1.AsSpan(0x54));
        if (entries.Length < entryCount * entrySize)
            return Error.Of(ErrorCode.BadFormat, $"entry array {entries.Length} < {entryCount} x {entrySize}");
        var diskId = new Guid(lba1.AsSpan(0x38, 16));

        var parts = new List<DiskPartition>();
        for (int i = 0; i < entryCount; i++)
        {
            var e = entries.AsSpan(i * entrySize, entrySize);
            var type = new Guid(e.Slice(0, 16));
            if (type == Guid.Empty) continue;
            long first = BinaryPrimitives.ReadInt64LittleEndian(e.Slice(0x20, 8));
            long last = BinaryPrimitives.ReadInt64LittleEndian(e.Slice(0x28, 8));
            if (first <= 0 || last < first) continue;
            var name = System.Text.Encoding.Unicode.GetString(e.Slice(0x38, Math.Min(72, e.Length - 0x38))).TrimEnd('\0');
            parts.Add(new DiskPartition(i + 1, first * sectorSize, (last - first + 1) * sectorSize, type, 0, name, null));
        }
        return (diskId, parts);
    }

    /// <summary>Pure MBR parse (protective/regular MBR partition table at 0x1BE).</summary>
    public static Result<List<DiskPartition>> ParseMbr(byte[] lba0, uint sectorSize = 512)
    {
        if (lba0.Length < 512) return Error.Of(ErrorCode.BadFormat, "MBR shorter than a sector");
        var parts = new List<DiskPartition>();
        for (int i = 0; i < 4; i++)
        {
            var e = lba0.AsSpan(0x1BE + i * 16, 16);
            byte type = e[4];
            if (type == 0) continue;
            uint first = BinaryPrimitives.ReadUInt32LittleEndian(e.Slice(8, 4));
            uint count = BinaryPrimitives.ReadUInt32LittleEndian(e.Slice(12, 4));
            if (first == 0 || count == 0) continue;
            parts.Add(new DiskPartition(i + 1, first * sectorSize, count * sectorSize, Guid.Empty, type, $"part{i + 1}", null));
        }
        return parts;
    }
}
