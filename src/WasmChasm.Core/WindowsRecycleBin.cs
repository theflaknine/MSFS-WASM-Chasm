using System.Runtime.InteropServices;
using System.Text;
using Microsoft.Win32;
using Microsoft.Win32.SafeHandles;

namespace WasmChasm.Core;

/// <summary>Recycle Bin via SHFileOperation with FOF_ALLOWUNDO. Never silently permanent-deletes.</summary>
public sealed class WindowsRecycleBin : IRecycleBin
{
    private const int MaxShellPath = 259;

    public string? WhyCannotRecycle(string path, long sizeBytes)
    {
        if (path.Length > MaxShellPath)
            return "Path is too long for the Recycle Bin.";

        // The bin that matters is the one on the drive holding the data, not the drive named in the path:
        // a Store install moved to another drive reaches it through a junction under %LOCALAPPDATA%.
        var driveRoot = PhysicalDriveRoot(path);
        if (string.IsNullOrEmpty(driveRoot)) return "Path has no drive.";

        try
        {
            var drive = new DriveInfo(driveRoot);
            if (drive.DriveType != DriveType.Fixed) return "The Recycle Bin is not available on this drive.";
        }
        catch (ArgumentException) { return "Unknown drive."; }

        var volumeKey = VolumeRegistryKey(driveRoot);
        if (volumeKey is not null)
        {
            using var key = Registry.CurrentUser.OpenSubKey(volumeKey);
            if (key is not null)
            {
                if (key.GetValue("NukeOnDelete") is int nuke && nuke != 0)
                    return "The Recycle Bin is disabled for this drive.";
                if (key.GetValue("MaxCapacity") is int maxMb && maxMb >= 0 && sizeBytes > (long)maxMb * 1024 * 1024)
                    return $"Folder is larger than the Recycle Bin's limit ({maxMb} MB).";
            }
        }

        return null;
    }

    /// <summary>The drive root a path really lands on, following any links among its ancestors.</summary>
    internal static string? PhysicalDriveRoot(string path)
    {
        var namedRoot = Path.GetPathRoot(path);
        if (string.IsNullOrEmpty(namedRoot)) return namedRoot;

        // The nearest part of the path that exists decides it, since Windows resolves every link on the way there.
        for (var current = path; !string.IsNullOrEmpty(current); current = Path.GetDirectoryName(current))
        {
            var final = FinalPath(current);
            if (final is not null) return Path.GetPathRoot(final);
        }

        return namedRoot;
    }

    /// <summary>The real location of a path after every link is followed, or null if it can't be opened.</summary>
    private static string? FinalPath(string path)
    {
        var name = path.Length >= 260 && !path.StartsWith(@"\\", StringComparison.Ordinal) ? @"\\?\" + path : path;
        using var handle = CreateFileW(name, 0, FILE_SHARE_ALL, IntPtr.Zero, OPEN_EXISTING, FILE_FLAG_BACKUP_SEMANTICS, IntPtr.Zero);
        if (handle.IsInvalid) return null;

        var sb = new StringBuilder(32768);
        uint len = GetFinalPathNameByHandleW(handle, sb, (uint)sb.Capacity, 0);
        if (len == 0 || len >= sb.Capacity) return null;

        var result = sb.ToString();
        if (result.StartsWith(@"\\?\UNC\", StringComparison.OrdinalIgnoreCase)) return @"\\" + result.Substring(8);
        return result.StartsWith(@"\\?\", StringComparison.Ordinal) ? result.Substring(4) : result;
    }

    public void Recycle(string path)
    {
        var op = new SHFILEOPSTRUCTW
        {
            wFunc = FO_DELETE,
            pFrom = path + "\0\0",
            // FOF_WANTNUKEWARNING makes the shell ask rather than silently delete if recycling turns out impossible.
            fFlags = FOF_ALLOWUNDO | FOF_NOCONFIRMATION | FOF_SILENT | FOF_NOERRORUI | FOF_WANTNUKEWARNING,
        };

        int rc = SHFileOperationW(ref op);
        if (op.fAnyOperationsAborted)
            throw new RecycleBinUnavailableException("Recycling was declined; the folder could not go to the Recycle Bin.");
        if (rc != 0)
            throw new IOException($"Recycle Bin operation failed (shell error 0x{rc:X}). The folder may be in use or access was denied.");
        var check = PathEx.Extended(path);
        if (Directory.Exists(check) || File.Exists(check))
            throw new IOException("The item is still present after recycling; it may be in use.");
    }

    private static string? VolumeRegistryKey(string driveRoot)
    {
        var sb = new StringBuilder(64);
        if (!GetVolumeNameForVolumeMountPointW(driveRoot, sb, (uint)sb.Capacity)) return null;
        var name = sb.ToString(); // \\?\Volume{guid}\
        int open = name.IndexOf('{');
        int close = name.IndexOf('}');
        if (open < 0 || close < open) return null;
        return @"Software\Microsoft\Windows\CurrentVersion\Explorer\BitBucket\Volume\" + name.Substring(open, close - open + 1);
    }

    private const uint FO_DELETE = 0x0003;
    private const ushort FOF_SILENT = 0x0004;
    private const ushort FOF_NOCONFIRMATION = 0x0010;
    private const ushort FOF_ALLOWUNDO = 0x0040;
    private const ushort FOF_NOERRORUI = 0x0400;
    private const ushort FOF_WANTNUKEWARNING = 0x4000;

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct SHFILEOPSTRUCTW
    {
        public IntPtr hwnd;
        public uint wFunc;
        [MarshalAs(UnmanagedType.LPWStr)] public string pFrom;
        [MarshalAs(UnmanagedType.LPWStr)] public string? pTo;
        public ushort fFlags;
        [MarshalAs(UnmanagedType.Bool)] public bool fAnyOperationsAborted;
        public IntPtr hNameMappings;
        [MarshalAs(UnmanagedType.LPWStr)] public string? lpszProgressTitle;
    }

    private const uint FILE_SHARE_ALL = 0x1 | 0x2 | 0x4;
    private const uint OPEN_EXISTING = 3;
    private const uint FILE_FLAG_BACKUP_SEMANTICS = 0x02000000;

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern SafeFileHandle CreateFileW(string name, uint access, uint share, IntPtr security, uint disposition, uint flags, IntPtr template);

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern uint GetFinalPathNameByHandleW(SafeFileHandle file, StringBuilder path, uint length, uint flags);

    [DllImport("shell32.dll", CharSet = CharSet.Unicode)]
    private static extern int SHFileOperationW(ref SHFILEOPSTRUCTW lpFileOp);

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetVolumeNameForVolumeMountPointW(string mountPoint, StringBuilder volumeName, uint bufferLength);
}

/// <summary>Used off Windows: there is no Recycle Bin, so recycling is always "unavailable".</summary>
public sealed class UnsupportedRecycleBin : IRecycleBin
{
    public string? WhyCannotRecycle(string path, long sizeBytes) => "No Recycle Bin on this platform.";
    public void Recycle(string path) => throw new RecycleBinUnavailableException("No Recycle Bin on this platform.");
}

public static class RecycleBins
{
    public static IRecycleBin ForCurrentPlatform() =>
        OperatingSystem.IsWindows() ? new WindowsRecycleBin() : new UnsupportedRecycleBin();
}
