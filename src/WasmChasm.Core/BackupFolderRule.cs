namespace WasmChasm.Core;

/// <summary>Checks a backup folder path that the user pasted or typed.</summary>
public static class BackupFolderRule
{
    /// <summary>Returns null and the cleaned-up folder if the path can be used, or a message saying why it can't.</summary>
    public static string? Check(string? input, IEnumerable<string> wasmRoots, out string? folder)
    {
        folder = null;

        // Windows' "Copy as path" puts quotes around the path.
        var text = (input ?? "").Trim().Trim('"').Trim();
        if (text.Length == 0) return "NO PATH WAS ENTERED.";
        if (text.StartsWith(@"\\?\", StringComparison.Ordinal) || !PathEx.IsPathFullyQualified(text))
            return @"ENTER A FULL PATH, SUCH AS D:\WASM BACKUPS.";

        string full;
        try { full = WithoutTrailingSeparators(Path.GetFullPath(text)); }
        catch (Exception ex) when (ex is ArgumentException or NotSupportedException or IOException)
        {
            return "THAT PATH IS NOT VALID.";
        }

        // A backup inside the WASM folder could be purged along with the data it protects.
        foreach (var root in wasmRoots)
        {
            var wasm = WithoutTrailingSeparators(Path.GetFullPath(root));
            if (string.Equals(full, wasm, StringComparison.OrdinalIgnoreCase) ||
                full.StartsWith(wasm + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase))
            {
                return "THE BACKUP FOLDER CANNOT BE INSIDE THE WASM FOLDER.";
            }
        }

        folder = full;
        return null;
    }

    /// <summary>Removes every trailing slash, of either kind, but keeps the one that a drive root such as "D:\" needs.</summary>
    private static string WithoutTrailingSeparators(string path)
    {
        while (true)
        {
            var trimmed = PathEx.TrimEndingDirectorySeparator(path);
            if (trimmed.Length == path.Length) return path;
            path = trimmed;
        }
    }
}
