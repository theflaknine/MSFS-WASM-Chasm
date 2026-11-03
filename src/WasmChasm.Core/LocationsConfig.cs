using System.Reflection;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace WasmChasm.Core;

/// <summary>Strongly typed view of data/locations.json.</summary>
public sealed class LocationsConfig
{
    [JsonPropertyName("schemaVersion")] public int SchemaVersion { get; set; } = 1;
    [JsonPropertyName("simProcessNames")] public List<string> SimProcessNames { get; set; } = new();
    [JsonPropertyName("knownSimVersionFolders")] public List<string> KnownSimVersionFolders { get; set; } = new();
    [JsonPropertyName("workFolderName")] public string WorkFolderName { get; set; } = "work";
    [JsonPropertyName("compiledCachePattern")] public string CompiledCachePattern { get; set; } = DefaultCompiledCachePattern;
    [JsonPropertyName("stores")] public List<StoreDefinition> Stores { get; set; } = new();
    [JsonPropertyName("manifestSearch")] public List<string> ManifestSearch { get; set; } = new();

    public const string FileName = "locations.json";
    public const string DefaultCompiledCachePattern = @"^m[0-9a-f]{16}_\d+\.(cache|dll|lib|natvis|obj|pdb)$";

    /// <summary>
    /// True for a file name the sim generates when it compiles a module. An invalid pattern (in a hand-edited
    /// locations.json) matches nothing, so a bad edit can only ever remove less.
    /// </summary>
    public bool IsCompiledCacheFile(string fileName)
    {
        try
        {
            return System.Text.RegularExpressions.Regex.IsMatch(
                fileName, CompiledCachePattern,
                System.Text.RegularExpressions.RegexOptions.IgnoreCase | System.Text.RegularExpressions.RegexOptions.CultureInvariant);
        }
        catch (ArgumentException)
        {
            return false;
        }
    }

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        ReadCommentHandling = JsonCommentHandling.Skip,
        AllowTrailingCommas = true,
        PropertyNameCaseInsensitive = true,
    };

    public static LocationsConfig Parse(string json) =>
        JsonSerializer.Deserialize<LocationsConfig>(json, JsonOptions)
        ?? throw new InvalidDataException("locations.json is empty.");

    /// <summary>
    /// Loads locations.json from beside the executable if present, else the embedded default.
    /// </summary>
    public static LocationsConfig Load(string? overridePath = null)
    {
        var candidates = new List<string>();
        if (overridePath is not null) candidates.Add(overridePath);
        candidates.Add(Path.Combine(AppContext.BaseDirectory, FileName));

        foreach (var path in candidates)
        {
            if (File.Exists(path)) return Parse(File.ReadAllText(path));
        }

        return LoadEmbedded();
    }

    public static LocationsConfig LoadEmbedded()
    {
        using var stream = typeof(LocationsConfig).Assembly.GetManifestResourceStream("WasmChasm.Core.locations.json")
            ?? throw new InvalidOperationException("Embedded locations.json missing.");
        using var reader = new StreamReader(stream);
        return Parse(reader.ReadToEnd());
    }

    public bool IsKnownSimVersion(string folderName) =>
        KnownSimVersionFolders.Any(k => string.Equals(k, folderName, StringComparison.OrdinalIgnoreCase));
}

public sealed class StoreDefinition
{
    [JsonPropertyName("id")] public string Id { get; set; } = "";
    [JsonPropertyName("label")] public string Label { get; set; } = "";
    [JsonPropertyName("wasmRoot")] public string WasmRoot { get; set; } = "";
    [JsonPropertyName("userCfg")] public string? UserCfg { get; set; }
    [JsonPropertyName("verified")] public bool Verified { get; set; }
}

/// <summary>Expands %VAR% tokens and normalises separators for the current OS.</summary>
public static class PathTemplate
{
    public static string? Expand(string template, Func<string, string?> getEnv)
    {
        var result = new System.Text.StringBuilder();
        int i = 0;
        while (i < template.Length)
        {
            if (template[i] == '%')
            {
                int end = template.IndexOf('%', i + 1);
                if (end < 0) return null;
                var name = template.Substring(i + 1, end - i - 1);
                var value = getEnv(name);
                if (string.IsNullOrEmpty(value)) return null;
                result.Append(value);
                i = end + 1;
            }
            else
            {
                result.Append(template[i]);
                i++;
            }
        }

        return Normalise(result.ToString());
    }

    public static string Normalise(string path) =>
        Path.DirectorySeparatorChar == '\\' ? path.Replace('/', '\\') : path.Replace('\\', '/');
}
