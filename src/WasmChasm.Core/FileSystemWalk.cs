namespace WasmChasm.Core;

/// <summary>Directory walking that never follows reparse points (FR-13).</summary>
public static class FileSystemWalk
{
    private static readonly EnumerationOptions Shallow = new()
    {
        RecurseSubdirectories = false,
        IgnoreInaccessible = true,
        AttributesToSkip = 0,
        ReturnSpecialDirectories = false,
    };

    public static bool IsReparsePoint(FileSystemInfo info)
    {
        try
        {
            return (info.Exists && (info.Attributes & FileAttributes.ReparsePoint) != 0) || info.LinkTarget is not null;
        }
        catch (IOException) { return false; }
        catch (UnauthorizedAccessException) { return false; }
    }

    public static bool IsReparsePoint(string path)
    {
        var info = new DirectoryInfo(path);
        if (info.Exists) return IsReparsePoint(info);
        var file = new FileInfo(path);
        return file.Exists && IsReparsePoint(file);
    }

    /// <summary>Total bytes and newest write time under a directory; reparse points count as zero bytes and are not entered.</summary>
    public static (long Bytes, DateTime LastWriteUtc) Measure(DirectoryInfo dir)
    {
        long bytes = 0;
        DateTime newest = SafeLastWrite(dir);
        if (IsReparsePoint(dir)) return (0, newest);

        var stack = new Stack<DirectoryInfo>();
        stack.Push(dir);
        while (stack.Count > 0)
        {
            var current = stack.Pop();
            IEnumerable<FileSystemInfo> children;
            try { children = current.EnumerateFileSystemInfos("*", Shallow).ToList(); }
            catch (IOException) { continue; }
            catch (UnauthorizedAccessException) { continue; }

            foreach (var child in children)
            {
                var lw = SafeLastWrite(child);
                if (lw > newest) newest = lw;

                if (IsReparsePoint(child)) continue;
                if (child is FileInfo f)
                {
                    try { bytes += f.Length; } catch (IOException) { }
                }
                else if (child is DirectoryInfo d)
                {
                    stack.Push(d);
                }
            }
        }

        return (bytes, newest);
    }

    private static DateTime SafeLastWrite(FileSystemInfo info)
    {
        try { return info.LastWriteTimeUtc; }
        catch (IOException) { return DateTime.MinValue; }
        catch (UnauthorizedAccessException) { return DateTime.MinValue; }
    }

    public static IEnumerable<DirectoryInfo> SubDirectories(DirectoryInfo dir)
    {
        try { return dir.EnumerateDirectories("*", Shallow).ToList(); }
        catch (IOException) { return Array.Empty<DirectoryInfo>(); }
        catch (UnauthorizedAccessException) { return Array.Empty<DirectoryInfo>(); }
    }
}
