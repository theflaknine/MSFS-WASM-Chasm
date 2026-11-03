using System.Text.Json;
using System.Text.RegularExpressions;

namespace WasmChasm.Core;

/// <summary>Maps a package ID to its display title.</summary>
public interface IManifestResolver
{
    /// <summary>Returns the manifest title, or null if none could be read.</summary>
    string? ResolveTitle(string packageId);
}

/// <summary>Resolver that never finds anything (used when no UserCfg.opt is available).</summary>
public sealed class NullManifestResolver : IManifestResolver
{
    public static readonly NullManifestResolver Instance = new();
    public string? ResolveTitle(string packageId) => null;
}

/// <summary>Reads InstalledPackagesPath from UserCfg.opt.</summary>
public static partial class UserCfgParser
{
    [GeneratedRegex("^\\s*InstalledPackagesPath\\s+\"?(?<path>[^\"]+?)\"?\\s*$", RegexOptions.Multiline | RegexOptions.IgnoreCase)]
    private static partial Regex InstalledPackagesPathRegex();

    public static string? ParseInstalledPackagesPath(string userCfgContents)
    {
        var match = InstalledPackagesPathRegex().Match(userCfgContents);
        return match.Success ? PathTemplate.Normalise(match.Groups["path"].Value.Trim()) : null;
    }

    public static string? ReadInstalledPackagesPath(string? userCfgPath)
    {
        if (userCfgPath is null || !File.Exists(userCfgPath)) return null;
        try { return ParseInstalledPackagesPath(File.ReadAllText(userCfgPath)); }
        catch (IOException) { return null; }
        catch (UnauthorizedAccessException) { return null; }
    }
}

/// <summary>
/// FR-3: resolves titles from manifest.json under the InstalledPackagesPath, searching Community,
/// Official and StreamedPackages per the patterns in locations.json.
/// </summary>
public sealed class ManifestResolver : IManifestResolver
{
    private readonly string _packagesRoot;
    private readonly IReadOnlyList<string> _patterns;
    private readonly Dictionary<string, string?> _cache = new(StringComparer.OrdinalIgnoreCase);

    public ManifestResolver(string packagesRoot, IEnumerable<string> searchPatterns)
    {
        _packagesRoot = packagesRoot;
        _patterns = searchPatterns.Select(PathTemplate.Normalise).ToList();
    }

    public static IManifestResolver ForInstall(InstallLocation install, LocationsConfig config)
    {
        var packages = UserCfgParser.ReadInstalledPackagesPath(install.UserCfgPath);
        return packages is not null && Directory.Exists(packages)
            ? new ManifestResolver(packages, config.ManifestSearch)
            : NullManifestResolver.Instance;
    }

    public string? ResolveTitle(string packageId)
    {
        if (_cache.TryGetValue(packageId, out var cached)) return cached;
        var title = Find(packageId);
        _cache[packageId] = title;
        return title;
    }

    private string? Find(string packageId)
    {
        // Package IDs are folder names; refuse anything that could escape the packages root.
        if (packageId.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0 || packageId is "." or "..") return null;

        foreach (var pattern in _patterns)
        {
            foreach (var dir in ExpandPattern(pattern.Replace("{package}", packageId)))
            {
                var manifest = Path.Combine(dir, "manifest.json");
                var title = ReadTitle(manifest);
                if (title is not null) return title;
            }
        }

        return null;
    }

    /// <summary>Expands a relative pattern where a segment may be a lone "*".</summary>
    private IEnumerable<string> ExpandPattern(string pattern)
    {
        var segments = pattern.Split(Path.DirectorySeparatorChar, StringSplitOptions.RemoveEmptyEntries);
        IEnumerable<string> current = new[] { _packagesRoot };
        foreach (var segment in segments)
        {
            current = segment == "*"
                ? current.SelectMany(d => FileSystemWalk.SubDirectories(new DirectoryInfo(d)).Select(s => s.FullName))
                : current.Select(d => Path.Combine(d, segment));
        }

        return current.Where(Directory.Exists);
    }

    internal static string? ReadTitle(string manifestPath)
    {
        if (!File.Exists(manifestPath)) return null;
        try
        {
            using var stream = File.OpenRead(manifestPath);
            using var doc = JsonDocument.Parse(stream, new JsonDocumentOptions
            {
                AllowTrailingCommas = true,
                CommentHandling = JsonCommentHandling.Skip,
            });
            if (doc.RootElement.ValueKind == JsonValueKind.Object &&
                doc.RootElement.TryGetProperty("title", out var title) &&
                title.ValueKind == JsonValueKind.String)
            {
                var s = title.GetString()?.Trim();
                return string.IsNullOrEmpty(s) ? null : s;
            }
        }
        catch (JsonException) { }
        catch (IOException) { }
        catch (UnauthorizedAccessException) { }

        return null;
    }
}
