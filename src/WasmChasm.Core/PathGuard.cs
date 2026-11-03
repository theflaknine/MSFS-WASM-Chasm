using System.Runtime.InteropServices;
using System.Text;
using System.Text.RegularExpressions;

namespace WasmChasm.Core;

public sealed record GuardDecision(bool Allowed, string? CanonicalPath, string Reason)
{
    public static GuardDecision Refuse(string reason) => new(false, null, reason);
}

/// <summary>Canonicalises and validates every target before any write (FR-12, FR-13).</summary>
public interface IPathGuard
{
    IReadOnlyList<string> Roots { get; }
    GuardDecision Check(string path);
}

public sealed class PathGuard : IPathGuard
{
    /// <summary>Targets must sit at least this many levels below a root (simVersion\package).</summary>
    public const int MinimumDepth = 2;

    private readonly List<string> _roots;
    private static readonly StringComparison Cmp =
        OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;

    public PathGuard(IEnumerable<string> roots)
    {
        _roots = new List<string>();
        foreach (var root in roots)
        {
            if (string.IsNullOrWhiteSpace(root) || !PathEx.IsPathFullyQualified(root))
                throw new ArgumentException($"Guard root must be a fully qualified path: '{root}'.");
            _roots.Add(PathEx.TrimEndingDirectorySeparator(LongName(Path.GetFullPath(root))));
        }
    }

    public IReadOnlyList<string> Roots => _roots;

    private static readonly Regex ShortNameRegex = new("~\\d");
    private static Regex ShortNamePattern() => ShortNameRegex;

    public GuardDecision Check(string path)
    {
        if (string.IsNullOrWhiteSpace(path)) return GuardDecision.Refuse("Empty path.");
        if (path.IndexOf('\0') >= 0) return GuardDecision.Refuse("Path contains a null character.");
        if (path.StartsWith(@"\\", StringComparison.Ordinal) || path.StartsWith("//", StringComparison.Ordinal))
            return GuardDecision.Refuse("UNC and device paths are not allowed.");
        if (!PathEx.IsPathFullyQualified(path)) return GuardDecision.Refuse("Path is not fully qualified.");

        var segments = path.Split(new[] { '\\', '/' }, StringSplitOptions.RemoveEmptyEntries);
        foreach (var segment in segments.Skip(OperatingSystem.IsWindows() ? 1 : 0))
        {
            if (segment is "." or "..") return GuardDecision.Refuse("Relative segments ('.' or '..') are not allowed.");
            if (OperatingSystem.IsWindows())
            {
                if (segment.EndsWith('.') || segment.EndsWith(' '))
                    return GuardDecision.Refuse("Segments ending in '.' or space are not allowed.");
                if (segment.Contains(':')) return GuardDecision.Refuse("Alternate data streams are not allowed.");
            }
        }

        string full;
        try { full = PathEx.TrimEndingDirectorySeparator(Path.GetFullPath(path)); }
        catch (Exception ex) when (ex is ArgumentException or NotSupportedException or PathTooLongException)
        {
            return GuardDecision.Refuse($"Path could not be canonicalised: {ex.Message}");
        }

        full = LongName(full);
        if (OperatingSystem.IsWindows() && ShortNamePattern().IsMatch(full) && !Directory.Exists(PathEx.Extended(full)) && !File.Exists(PathEx.Extended(full)))
        {
            // An 8.3 name we could not expand to its long form: be conservative.
            return GuardDecision.Refuse("Unresolvable 8.3 short name.");
        }

        var root = MatchingRoot(full);
        if (root is null) return GuardDecision.Refuse("Path is outside every detected WASM root.");

        var relative = full.Substring(root.Length).TrimStart(Path.DirectorySeparatorChar);
        var relSegments = relative.Split(Path.DirectorySeparatorChar, StringSplitOptions.RemoveEmptyEntries);
        if (relSegments.Length < MinimumDepth)
            return GuardDecision.Refuse("Refusing to remove a WASM root or sim-version folder.");

        // Every ancestor between the root and the target must be a real directory, not a link:
        // otherwise the target physically lives somewhere else.
        var cursor = root;
        for (int i = 0; i < relSegments.Length - 1; i++)
        {
            cursor = Path.Combine(cursor, relSegments[i]);
            if (FileSystemWalk.IsReparsePoint(cursor))
                return GuardDecision.Refuse($"Ancestor '{cursor}' is a reparse point.");
        }

        return new GuardDecision(true, full, "OK");
    }

    private string? MatchingRoot(string full)
    {
        foreach (var root in _roots)
        {
            if (full.Length > root.Length + 1 &&
                full.StartsWith(root, Cmp) &&
                full[root.Length] == Path.DirectorySeparatorChar)
            {
                return root;
            }
        }

        return null;
    }

    private static string LongName(string path)
    {
        if (!OperatingSystem.IsWindows() || !ShortNamePattern().IsMatch(path)) return path;
        var buffer = new StringBuilder(32768);
        uint len = GetLongPathNameW(path, buffer, (uint)buffer.Capacity);
        return len > 0 && len < buffer.Capacity ? buffer.ToString() : path;
    }

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern uint GetLongPathNameW(string shortPath, StringBuilder longPath, uint bufferLength);
}
