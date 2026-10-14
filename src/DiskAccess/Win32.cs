using System.ComponentModel;
using System.Runtime.InteropServices;
using OpenReflect.Core.Abstractions;

namespace OpenReflect.DiskAccess;

/// <summary>Win32 device I/O primitives. Error mapping follows the Results taxonomy:
/// access → SourceAccessDenied/DestinationAccessDenied, busy → DestinationInUse.</summary>
public static class Win32
{
    public const uint GENERIC_READ = 0x80000000;
    public const uint GENERIC_WRITE = 0x40000000;
    public const uint FILE_SHARE_READ = 0x1;
    public const uint FILE_SHARE_WRITE = 0x2;
    public const uint OPEN_EXISTING = 3;
    public const uint FILE_ATTRIBUTE_NORMAL = 0x80;
    public const uint FILE_FLAG_BACKUP_SEMANTICS = 0x02000000;
    public const uint FILE_FLAG_OVERLAPPED = 0x40000000;

    public const int IOCTL_DISK_BASE = 0x00000007;
    public const int FILE_DEVICE_FILE_SYSTEM = 0x00000009;
    public const int FILE_DEVICE_MASS_STORAGE = 0x0000002D;
    public const uint METHOD_BUFFERED = 0;
    public const uint FILE_ANY_ACCESS = 0;
    public const uint FILE_READ_ACCESS = 1;

    public static uint CTL_CODE(int deviceType, int function, uint method, uint access)
        => (uint)((deviceType << 16) | (access << 14) | (function << 2) | method);

    public static readonly uint IOCTL_DISK_GET_DRIVE_GEOMETRY = CTL_CODE(IOCTL_DISK_BASE, 0x0000, METHOD_BUFFERED, FILE_ANY_ACCESS);
    public static readonly uint IOCTL_DISK_GET_DRIVE_GEOMETRY_EX = CTL_CODE(IOCTL_DISK_BASE, 0x0028, METHOD_BUFFERED, FILE_ANY_ACCESS);
    public static readonly uint IOCTL_DISK_GET_LENGTH_INFO = CTL_CODE(IOCTL_DISK_BASE, 0x0011, METHOD_BUFFERED, FILE_READ_ACCESS);
    public static readonly uint IOCTL_STORAGE_GET_DEVICE_NUMBER = CTL_CODE(FILE_DEVICE_MASS_STORAGE, 0x0420, METHOD_BUFFERED, FILE_ANY_ACCESS);
    public static readonly uint FSCTL_DISMOUNT_VOLUME = CTL_CODE(FILE_DEVICE_FILE_SYSTEM, 0x0008, METHOD_BUFFERED, FILE_ANY_ACCESS);
    // FILE_DEVICE_VOLUME = 0x56; offline dismounts AND blocks mount-manager remounts
    public static readonly uint IOCTL_VOLUME_OFFLINE = CTL_CODE(0x00000056, 0x0002, METHOD_BUFFERED, FILE_ANY_ACCESS);
    public static readonly uint IOCTL_VOLUME_ONLINE = CTL_CODE(0x00000056, 0x0003, METHOD_BUFFERED, FILE_ANY_ACCESS);

    [StructLayout(LayoutKind.Sequential)]
    public struct DISK_GEOMETRY
    {
        public long Cylinders;
        public uint MediaType;
        public uint TracksPerCylinder;
        public uint SectorsPerTrack;
        public uint BytesPerSector;
    }

    [StructLayout(LayoutKind.Sequential)]
    public struct GET_LENGTH_INFORMATION { public long Length; }

    [StructLayout(LayoutKind.Sequential)]
    public struct STORAGE_DEVICE_NUMBER
    {
        public uint DeviceType;
        public uint DeviceNumber;
        public uint PartitionNumber;
    }

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    public static extern IntPtr CreateFileW(string name, uint access, uint share, IntPtr security,
        uint disposition, uint flags, IntPtr template);

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static extern bool CloseHandle(IntPtr handle);

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static extern bool DeviceIoControl(System.Runtime.InteropServices.SafeHandle device, uint code, IntPtr inBuffer, uint inSize,
        IntPtr outBuffer, uint outSize, out uint returned, IntPtr overlapped);

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    public static extern uint GetDriveTypeW(string root);

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static extern bool DeleteVolumeMountPointW(string mountPoint);

    /// <summary>Opens a Win32 path and wraps it in a SafeHandle; the caller maps errors.</summary>
    public static Result<Microsoft.Win32.SafeHandles.SafeFileHandle> Open(string win32Path, bool write, bool exclusive)
    {
        uint share = exclusive ? 0 : FILE_SHARE_READ | FILE_SHARE_WRITE;
        uint access = write ? GENERIC_READ | GENERIC_WRITE : GENERIC_READ;
        var h = CreateFileW(win32Path, access, share, IntPtr.Zero, OPEN_EXISTING, FILE_ATTRIBUTE_NORMAL | FILE_FLAG_OVERLAPPED, IntPtr.Zero);
        if (h == IntPtr.Zero || h == new IntPtr(-1))
        {
            var code = Marshal.GetLastWin32Error();
            return MapCreateError(code, win32Path);
        }
        return new Microsoft.Win32.SafeHandles.SafeFileHandle(h, ownsHandle: true);
    }

    private static Error MapCreateError(int code, string path)
    {
        var ex = new Win32Exception(code);
        return code switch
        {
            2 => Error.Of(ErrorCode.SourceNotFound, $"not found: {path}", ex.Message),
            3 => Error.Of(ErrorCode.SourceNotFound, $"path not found: {path}", ex.Message),
            5 => Error.Of(ErrorCode.SourceAccessDenied, $"access denied: {path} (elevation required)", ex.Message),
            32 or 33 => Error.Of(ErrorCode.DestinationInUse, $"device is in use (sharing violation): {path}", ex.Message),
            _ => Error.Of(ErrorCode.SourceReadError, $"cannot open {path}", ex.Message),
        };
    }

    public static Error LastError(string what) =>
        Error.Of(ErrorCode.SourceReadError, $"{what} failed", new Win32Exception(Marshal.GetLastWin32Error()).Message);


    public static Result<T> IoControl<T>(Microsoft.Win32.SafeHandles.SafeFileHandle device, uint code, int structSize) where T : struct
    {
        var buffer = Marshal.AllocHGlobal(Math.Max(structSize, 8));
        try
        {
            if (!DeviceIoControl(device, code, IntPtr.Zero, 0, buffer, (uint)structSize, out var returned, IntPtr.Zero))
                return LastError($"ioctl 0x{code:X}");
            if (returned < structSize) return Error.Of(ErrorCode.SourceReadError, $"ioctl 0x{code:X} returned {returned} bytes");
            var obj = Marshal.PtrToStructure(buffer, typeof(T));
            return (T)obj;
        }
        finally { Marshal.FreeHGlobal(buffer); }
    }

    /// <summary>FSCTL_DISMOUNT_VOLUME — no output buffer.</summary>
    public static Result DismountVolume(Microsoft.Win32.SafeHandles.SafeFileHandle device)
    {
        if (!DeviceIoControl(device, FSCTL_DISMOUNT_VOLUME, IntPtr.Zero, 0, IntPtr.Zero, 0, out _, IntPtr.Zero))
            return LastError("FSCTL_DISMOUNT_VOLUME");
        return Result.Ok();
    }

    /// <summary>Raw buffered ioctl returning the full output bytes (for variable structs).</summary>
    public static Result<byte[]> IoControlRaw(Microsoft.Win32.SafeHandles.SafeFileHandle device, uint code, int outSize)
    {
        var buffer = Marshal.AllocHGlobal(outSize);
        try
        {
            if (!DeviceIoControl(device, code, IntPtr.Zero, 0, buffer, (uint)outSize, out var returned, IntPtr.Zero))
                return LastError($"ioctl 0x{code:X}");
            var bytes = new byte[returned];
            Marshal.Copy(buffer, bytes, 0, (int)returned);
            return bytes;
        }
        finally { Marshal.FreeHGlobal(buffer); }
    }
}
