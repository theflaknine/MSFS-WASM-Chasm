namespace WasmChasm.Core;

/// <summary>.NET Framework is Windows only, so this keeps the platform checks readable.</summary>
public static class OperatingSystem
{
    public static bool IsWindows() => true;
}

/// <summary>Path helpers that .NET Framework lacks, with the same behaviour as modern .NET on Windows.</summary>
public static class PathEx
{
    /// <summary>True only for a drive path with a separator, or a UNC or device path. "C:foo" and "\foo" are not.</summary>
    public static bool IsPathFullyQualified(string path)
    {
        if (path is null) throw new ArgumentNullException(nameof(path));
        if (path.Length < 2) return false;
        if (IsSeparator(path[0])) return path[1] == '?' || IsSeparator(path[1]);
        return path.Length >= 3 && path[1] == ':' && IsSeparator(path[2]) && IsDriveLetter(path[0]);
    }

    /// <summary>Removes one trailing separator, unless that would change the meaning of a root such as "C:\".</summary>
    public static string TrimEndingDirectorySeparator(string path)
    {
        if (string.IsNullOrEmpty(path) || !IsSeparator(path[path.Length - 1])) return path;
        var root = Path.GetPathRoot(path);
        return path.Length > (root?.Length ?? 0) ? path.Substring(0, path.Length - 1) : path;
    }

    private const string ExtendedPrefix = @"\\?\";
    private const string ExtendedUncPrefix = @"\\?\UNC\";

    /// <summary>
    /// The form of a path to give the file system. .NET Framework fails on ordinary paths past Windows' length
    /// limit, unless the PC has opted in to long paths, and most haven't. Only use this at the moment of touching
    /// the disk: the path guard and anything saved or shown must use the ordinary form.
    /// </summary>
    public static string Extended(string path)
    {
        // 248 is the limit for a folder; a file's limit is higher, so this covers both.
        if (path.Length < 248 || path.StartsWith(ExtendedPrefix, StringComparison.Ordinal) || !IsPathFullyQualified(path)) return path;

        var full = Path.GetFullPath(path);
        return full.StartsWith(@"\\", StringComparison.Ordinal)
            ? ExtendedUncPrefix + full.Substring(2)
            : ExtendedPrefix + full;
    }

    /// <summary>The ordinary form of a path that may have come back from the file system in extended form.</summary>
    public static string Plain(string path)
    {
        if (path.StartsWith(ExtendedUncPrefix, StringComparison.OrdinalIgnoreCase)) return @"\\" + path.Substring(ExtendedUncPrefix.Length);
        return path.StartsWith(ExtendedPrefix, StringComparison.Ordinal) ? path.Substring(ExtendedPrefix.Length) : path;
    }

    private static bool IsSeparator(char c) => c == '\\' || c == '/';
    private static bool IsDriveLetter(char c) => (c >= 'A' && c <= 'Z') || (c >= 'a' && c <= 'z');
}

public static class StringCompat
{
    public static bool EndsWith(this string text, char value) => text.Length > 0 && text[text.Length - 1] == value;
    public static string[] Split(this string text, char separator, StringSplitOptions options) => text.Split(new[] { separator }, options);
}
