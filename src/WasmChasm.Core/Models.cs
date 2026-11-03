namespace WasmChasm.Core;

/// <summary>A detected (or user-chosen) sim install: where its WASM data and UserCfg.opt live.</summary>
public sealed record InstallLocation(string StoreId, string Label, string WasmRoot, string? UserCfgPath)
{
    public bool IsCustom => StoreId == CustomStoreId;
    public const string CustomStoreId = "custom";

    public static InstallLocation Custom(string wasmRoot) =>
        new(CustomStoreId, "CUSTOM", wasmRoot, null);
}

/// <summary>One package folder under a sim-version folder, e.g. WASM\MSFS2024\pmdg-aircraft-77w.</summary>
public sealed record WasmEntry(
    string PackageId,
    string SimVersionFolder,
    string PackagePath,
    string WorkPath,
    bool HasWorkFolder,
    long SizeBytes,
    long WorkSizeBytes,
    DateTime LastModifiedUtc,
    bool IsKnownSimVersion,
    bool WorkIsReparsePoint,
    IReadOnlyList<string>? CompiledFiles = null,
    long CompiledSizeBytes = 0)
{
    public bool HasCompiledCache => CompiledFiles is { Count: > 0 };

    /// <summary>Whether this entry has anything for the given scope to remove.</summary>
    public bool HasContentFor(DeletionScope scope) => scope switch
    {
        DeletionScope.CompiledCache => HasCompiledCache,
        DeletionScope.WorkFolder => HasWorkFolder,
        _ => true,
    };

    public long SizeFor(DeletionScope scope) => scope switch
    {
        DeletionScope.CompiledCache => CompiledSizeBytes,
        DeletionScope.WorkFolder => WorkSizeBytes,
        _ => SizeBytes,
    };
}

/// <summary>All entries for one package ID across sibling sim-version folders (FR-4).</summary>
public sealed class AircraftGroup
{
    public AircraftGroup(string packageId, string? title, IReadOnlyList<WasmEntry> entries)
    {
        PackageId = packageId;
        Title = title;
        Entries = entries;
    }

    public string PackageId { get; }

    /// <summary>Resolved manifest title, or null when none could be read: the display name is then the package ID.</summary>
    public string? Title { get; }

    public string DisplayName => Title ?? PackageId;
    public IReadOnlyList<WasmEntry> Entries { get; }

    public long SizeBytes => Entries.Sum(e => e.SizeBytes);
    public long WorkSizeBytes => Entries.Sum(e => e.WorkSizeBytes);
    public long CompiledSizeBytes => Entries.Sum(e => e.CompiledSizeBytes);
    public DateTime LastModifiedUtc => Entries.Count == 0 ? DateTime.MinValue : Entries.Max(e => e.LastModifiedUtc);

    /// <summary>Size of what a consignment with the given scope would remove (known folders only).</summary>
    public long ScopedSize(DeletionScope scope, bool includeUnknown = false) =>
        Entries.Where(e => includeUnknown || e.IsKnownSimVersion).Sum(e => e.SizeFor(scope));
}

/// <summary>FR-10: what gets removed for each entry.</summary>
public enum DeletionScope
{
    /// <summary>Only the sim-generated compiled module files beside the work folder (default).</summary>
    CompiledCache,
    /// <summary>Only the package folder's work subfolder: the add-on's persistent data.</summary>
    WorkFolder,
    /// <summary>The whole package folder.</summary>
    PackageFolder,
}

public enum DisposalMode
{
    RecycleBin,
    ZipThenRecycle,
    Permanent,
    /// <summary>Zip a backup, then delete permanently. Only offered when the Recycle Bin is unavailable.</summary>
    ZipThenPermanent,
}

public enum ConsignOutcome
{
    Removed,
    WouldRemove,
    Skipped,
    Failed,
}

/// <summary>
/// One folder queued for disposal or, when <see cref="Files"/> is set, those files inside the folder at
/// <see cref="Path"/>. In that case the folder itself and everything else in it stay.
/// </summary>
public sealed record ConsignTarget(string PackageId, string DisplayName, string SimVersionFolder, string Path, long SizeBytes, IReadOnlyList<string>? Files = null)
{
    /// <summary>The path for display and logs, saying plainly when only some files are affected.</summary>
    public string Label => Files is null ? Path : $"{Path} ({Files.Count} compiled files; work folder kept)";
}

public sealed record ConsignResult(
    ConsignTarget Target,
    ConsignOutcome Outcome,
    string Reason,
    bool LinkOnly = false,
    string? BackupPath = null);

public sealed record ConsignProgress(int Index, int Total, ConsignResult Result);

public static class ByteFormat
{
    public static string Format(long bytes)
    {
        const double k = 1024;
        if (bytes < k) return $"{bytes} B";
        if (bytes < k * k) return $"{bytes / k:0.#} KB";
        if (bytes < k * k * k) return $"{bytes / (k * k):0.#} MB";
        return $"{bytes / (k * k * k):0.##} GB";
    }

    public static string Megabytes(long bytes) => $"{bytes / (1024d * 1024d):0} MB";
}
