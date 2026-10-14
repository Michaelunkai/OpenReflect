using System.ComponentModel;
using System.Runtime.InteropServices;
using OpenReflect.Core.Abstractions;

namespace OpenReflect.DiskAccess;

public sealed record VolumeRef(string VolumeGuidPath, uint DiskNumber, uint PartitionNumber, string[] MountPaths, string FileSystem)
{
    public char? DriveLetter
    {
        get
        {
            foreach (var p in MountPaths)
                if (p.Length >= 2 && p[1] == ':') return char.ToUpperInvariant(p[0]);
            return null;
        }
    }
}

/// <summary>Enumerates \\?\Volume{guid} volumes and maps them to (disk, partition) via
/// IOCTL_STORAGE_GET_DEVICE_NUMBER. Used for VSS volume selection, OS-on-target detection and
/// the `disks` command. Drive letters resolve through GetVolumeNameForVolumeMountPointW because
/// GetVolumePathNamesForVolumeNameW returns ERROR_INVALID_PARAMETER on this stack.</summary>
public static class VolumeEnumerator
{
    public static Result<List<VolumeRef>> All()
    {
        var list = new List<VolumeRef>();
        var letterMap = LetterMounts();
        var buffer = new char[120];
        var find = Native.FindFirstVolumeW(buffer, (uint)buffer.Length);
        if (find.IsInvalid)
            return Error.Of(ErrorCode.SourceReadError, "FindFirstVolume failed", new Win32Exception(Marshal.GetLastWin32Error()).Message);
        using (find)
        {
            bool more = true;
            while (more)
            {
                var guidPath = new string(buffer).TrimEnd('\0');
                var info = Describe(guidPath, letterMap);
                if (info is not null) list.Add(info);
                more = Native.FindNextVolumeW(find, buffer, (uint)buffer.Length);
            }
            // trailing GetLastError=ERROR_NO_MORE_FILES is the normal end; anything else ignored
        }
        return list;
    }

    public static VolumeRef? Describe(string volumeGuidPath) => Describe(volumeGuidPath, LetterMounts());

    private static VolumeRef? Describe(string volumeGuidPath, Dictionary<string, string[]> letterMap)
    {
        if (!volumeGuidPath.StartsWith(@"\\?\", StringComparison.OrdinalIgnoreCase)) return null;
        var opened = Win32.Open(volumeGuidPath.TrimEnd('\\'), write: false, exclusive: false);
        try
        {
            if (!opened.IsSuccess) return null;
            using var handle = opened.Value;
            if (!Native.GetDeviceNumber(handle, out var sdn))
                return null;

            var mounts = letterMap.GetValueOrDefault(volumeGuidPath, Array.Empty<string>());

            var fsBuf = new char[32];
            string fs = "";
            if (Native.GetVolumeInformationW(volumeGuidPath, null, 0, out _, out _, out _, fsBuf, (uint)fsBuf.Length))
                fs = new string(fsBuf).TrimEnd('\0').ToUpperInvariant();

            return new VolumeRef(volumeGuidPath, sdn.DeviceNumber, sdn.PartitionNumber, mounts, fs);
        }
        catch
        {
            return null;
        }
    }

    /// <summary>All volumes living on one disk (by device number).</summary>
    public static Result<List<VolumeRef>> ForDisk(uint diskNumber)
    {
        var all = All();
        if (!all.IsSuccess) return Result<List<VolumeRef>>.Fail(all.Error);
        return all.Value.Where(v => v.DiskNumber == diskNumber).ToList();
    }

    /// <summary>The volume that hosts the RUNNING operating system root (by %SystemDrive%).</summary>
    public static string? RunningOsVolumeGuid()
    {
        var root = Path.GetPathRoot(Environment.SystemDirectory);
        if (string.IsNullOrEmpty(root)) return null;
        var wanted = root.EndsWith('\\') ? root : root + "\\";
        var all = All();
        if (!all.IsSuccess) return null;
        foreach (var v in all.Value)
            foreach (var m in v.MountPaths)
                if (string.Equals(m, wanted, StringComparison.OrdinalIgnoreCase)) return v.VolumeGuidPath;
        return null;
    }

    /// <summary>Maps volume GUID path (with trailing backslash) → the drive-letter mount paths
    /// that point at it. One consistent snapshot per call.</summary>
    public static Dictionary<string, string[]> LetterMounts()
    {
        var map = new Dictionary<string, string[]>(StringComparer.OrdinalIgnoreCase);
        var letters = new char[512];
        uint len = Native.GetLogicalDriveStringsW((uint)letters.Length, letters);
        if (len == 0 || len >= letters.Length) return map;
        for (int off = 0; off < len;)
        {
            string drive = new string(letters, off, 3);   // "C:\"
            off += 4;
            if (drive.Length < 3 || drive[1] != ':') continue;
            var g = new char[80];
            if (Native.GetVolumeNameForVolumeMountPointW(drive, g, (uint)g.Length))
            {
                var guidPath = new string(g).TrimEnd('\0');
                if (guidPath.StartsWith(@"\\?\"))
                    map[guidPath] = map.GetValueOrDefault(guidPath, Array.Empty<string>()).Append(drive).ToArray();
            }
        }
        return map;
    }

    private static unsafe class Native
    {
        [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
        internal static extern Microsoft.Win32.SafeHandles.SafeFileHandle FindFirstVolumeW([Out] char[] volumeName, uint bufferLength);

        [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        internal static extern bool FindNextVolumeW(Microsoft.Win32.SafeHandles.SafeFileHandle find, [Out] char[] volumeName, uint bufferLength);

        [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
        internal static extern uint GetLogicalDriveStringsW(uint bufferLength, [Out] char[] buffer);

        [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        internal static extern bool GetVolumeNameForVolumeMountPointW(string mountPoint, [Out] char[] volumeName, uint bufferLength);

        [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        internal static extern bool GetVolumeInformationW(string root, System.Text.StringBuilder? name, uint nameSize,
            out uint serial, out uint maxLen, out uint flags, [Out] char[] fsName, uint fsSize);

        internal static unsafe bool GetDeviceNumber(System.Runtime.InteropServices.SafeHandle handle, out Win32.STORAGE_DEVICE_NUMBER sdn)
        {
            var tmp = new Win32.STORAGE_DEVICE_NUMBER();
            bool ok = DeviceIoControl(handle, Win32.IOCTL_STORAGE_GET_DEVICE_NUMBER, IntPtr.Zero, 0,
                &tmp, (uint)System.Runtime.InteropServices.Marshal.SizeOf<Win32.STORAGE_DEVICE_NUMBER>(), out _, IntPtr.Zero);
            if (ok) sdn = tmp;
            else sdn = default;
            return ok;
        }

        [DllImport("kernel32.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static extern bool DeviceIoControl(System.Runtime.InteropServices.SafeHandle device, uint code, IntPtr inBuf, uint inSize, void* outBuf, uint outSize, out uint returned, IntPtr overlapped);
    }
}
