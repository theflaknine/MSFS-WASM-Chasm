namespace WasmChasm.Core;

/// <summary>Directory walking that never follows reparse points (FR-13).</summary>
public static class FileSystemWalk
{
    public static bool IsReparsePoint(FileSystemInfo info) => IsReparsePoint(info.FullName);

    /// <summary>Reads the item's own attributes without following it, so a broken link still counts as a link.</summary>
    public static bool IsReparsePoint(string path)
    {
        try { return (File.GetAttributes(PathEx.Extended(path)) & FileAttributes.ReparsePoint) != 0; }
        catch (IOException) { return false; }
        catch (UnauthorizedAccessException) { return false; }
    }

    /// <summary>Total bytes and newest write time under a directory; reparse points count as zero bytes and are not entered.</summary>
    public static (long Bytes, DateTime LastWriteUtc) Measure(DirectoryInfo dir)
    {
        dir = Reachable(dir);
        long bytes = 0;
        DateTime newest = SafeLastWrite(dir);
        if (IsReparsePoint(dir)) return (0, newest);

        var stack = new Stack<DirectoryInfo>();
        stack.Push(dir);
        while (stack.Count > 0)
        {
            var current = stack.Pop();
            IEnumerable<FileSystemInfo> children;
            try { children = Reachable(current).EnumerateFileSystemInfos("*").ToList(); }
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

    /// <summary>The same folder, in a form the file system can reach however long its path is.</summary>
    private static DirectoryInfo Reachable(DirectoryInfo dir)
    {
        var extended = PathEx.Extended(dir.FullName);
        return ReferenceEquals(extended, dir.FullName) ? dir : new DirectoryInfo(extended);
    }

    private static DateTime SafeLastWrite(FileSystemInfo info)
    {
        try { return info.LastWriteTimeUtc; }
        catch (IOException) { return DateTime.MinValue; }
        catch (UnauthorizedAccessException) { return DateTime.MinValue; }
    }

    public static IEnumerable<FileInfo> Files(DirectoryInfo dir)
    {
        try { return Reachable(dir).EnumerateFiles("*").ToList(); }
        catch (IOException) { return Array.Empty<FileInfo>(); }
        catch (UnauthorizedAccessException) { return Array.Empty<FileInfo>(); }
    }

    public static IEnumerable<DirectoryInfo> SubDirectories(DirectoryInfo dir)
    {
        try { return Reachable(dir).EnumerateDirectories("*").ToList(); }
        catch (IOException) { return Array.Empty<DirectoryInfo>(); }
        catch (UnauthorizedAccessException) { return Array.Empty<DirectoryInfo>(); }
    }
}
