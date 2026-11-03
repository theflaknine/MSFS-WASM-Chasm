using System.Text.Json;
using System.Text.Json.Serialization;

namespace WasmChasm.Core;

/// <summary>User preferences, stored as JSON in %LOCALAPPDATA%\WasmChasm\settings.json.</summary>
public sealed class AppSettings
{
    public string? PreferredStoreId { get; set; }
    public string? CustomWasmRoot { get; set; }
    public bool CrtEffects { get; set; } = true;
    public bool Sound { get; set; }
    public DisposalMode DisposalMode { get; set; } = DisposalMode.RecycleBin;
    public string? BackupFolder { get; set; }
    public DeletionScope Scope { get; set; } = DeletionScope.CompiledCache;
    public bool DryRun { get; set; }

    private static readonly JsonSerializerOptions Options = new()
    {
        WriteIndented = true,
        Converters = { new JsonStringEnumConverter() },
    };

    public static string DefaultPath => Path.Combine(SessionLog.DefaultDirectory, "settings.json");

    public static AppSettings Load(string? path = null)
    {
        path ??= DefaultPath;
        try
        {
            if (File.Exists(path))
                return JsonSerializer.Deserialize<AppSettings>(File.ReadAllText(path), Options) ?? new AppSettings();
        }
        catch (Exception ex) when (ex is IOException or JsonException or UnauthorizedAccessException) { }

        return new AppSettings();
    }

    public void Save(string? path = null)
    {
        path ??= DefaultPath;
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            File.WriteAllText(path, JsonSerializer.Serialize(this, Options));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
    }
}
