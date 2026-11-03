namespace WasmChasm.Core;

/// <summary>Walks a WASM root and returns one <see cref="WasmEntry"/> per package folder.</summary>
public interface IWasmScanner
{
    IReadOnlyList<WasmEntry> Scan(string wasmRoot, CancellationToken cancellationToken = default);
}

public sealed class WasmScanner : IWasmScanner
{
    private readonly LocationsConfig _config;

    public WasmScanner(LocationsConfig config) => _config = config;

    /// <summary>FR-2: every package folder under every sim-version subfolder (known or not).</summary>
    public IReadOnlyList<WasmEntry> Scan(string wasmRoot, CancellationToken cancellationToken = default)
    {
        var root = new DirectoryInfo(PathEx.Extended(wasmRoot));
        if (!root.Exists) return Array.Empty<WasmEntry>();

        var entries = new List<WasmEntry>();
        foreach (var simDir in FileSystemWalk.SubDirectories(root))
        {
            cancellationToken.ThrowIfCancellationRequested();
            // A reparse point at sim-version level would lead outside the root: never enter it.
            if (FileSystemWalk.IsReparsePoint(simDir)) continue;

            bool known = _config.IsKnownSimVersion(simDir.Name);
            foreach (var pkgDir in FileSystemWalk.SubDirectories(simDir))
            {
                cancellationToken.ThrowIfCancellationRequested();
                entries.Add(ScanPackage(simDir.Name, pkgDir, known));
            }
        }

        return entries
            .OrderBy(e => e.PackageId, StringComparer.OrdinalIgnoreCase)
            .ThenBy(e => e.SimVersionFolder, StringComparer.OrdinalIgnoreCase)
            .ToList();
    }

    private WasmEntry ScanPackage(string simVersion, DirectoryInfo pkgDir, bool known)
    {
        var workPath = Path.Combine(PathEx.Plain(pkgDir.FullName), _config.WorkFolderName);
        var workDir = new DirectoryInfo(PathEx.Extended(workPath));
        bool pkgIsLink = FileSystemWalk.IsReparsePoint(pkgDir);
        bool hasWork = !pkgIsLink && (workDir.Exists || FileSystemWalk.IsReparsePoint(workDir.FullName));
        bool workIsLink = hasWork && FileSystemWalk.IsReparsePoint(workDir);

        var (pkgBytes, pkgWrite) = FileSystemWalk.Measure(pkgDir);
        long workBytes = 0;
        if (hasWork && !workIsLink) (workBytes, _) = FileSystemWalk.Measure(workDir);

        // Only real files directly in the package folder, by name. Links are never treated as cache.
        var compiled = pkgIsLink
            ? new List<FileInfo>()
            : FileSystemWalk.Files(pkgDir)
                .Where(f => _config.IsCompiledCacheFile(f.Name) && !FileSystemWalk.IsReparsePoint(f))
                .ToList();

        return new WasmEntry(
            PackageId: pkgDir.Name,
            SimVersionFolder: simVersion,
            PackagePath: PathEx.Plain(pkgDir.FullName),
            WorkPath: workPath,
            HasWorkFolder: hasWork,
            SizeBytes: pkgBytes,
            WorkSizeBytes: workBytes,
            LastModifiedUtc: pkgWrite,
            IsKnownSimVersion: known,
            WorkIsReparsePoint: workIsLink || pkgIsLink,
            CompiledFiles: compiled.Select(f => PathEx.Plain(f.FullName)).ToList(),
            CompiledSizeBytes: compiled.Sum(f => f.Length));
    }

    /// <summary>FR-4: groups entries by package ID across sibling folders.</summary>
    public static IReadOnlyList<AircraftGroup> Group(IEnumerable<WasmEntry> entries, IManifestResolver resolver) =>
        entries
            .GroupBy(e => e.PackageId, StringComparer.OrdinalIgnoreCase)
            .Select(g => new AircraftGroup(g.Key, resolver.ResolveTitle(g.Key), g.ToList()))
            .OrderBy(g => g.DisplayName, StringComparer.OrdinalIgnoreCase)
            .ToList();
}
