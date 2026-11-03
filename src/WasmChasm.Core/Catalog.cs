namespace WasmChasm.Core;

/// <summary>Locate → scan → resolve names, in one call. Shared by the CLI and the app.</summary>
public static class Catalog
{
    public static IReadOnlyList<AircraftGroup> Load(InstallLocation install, LocationsConfig config, CancellationToken ct = default)
    {
        var entries = new WasmScanner(config).Scan(install.WasmRoot, ct);
        var resolver = ManifestResolver.ForInstall(install, config);
        return WasmScanner.Group(entries, resolver);
    }

    /// <summary>The path guard for an install is anchored to its WASM root only.</summary>
    public static IPathGuard GuardFor(InstallLocation install) => new PathGuard(new[] { install.WasmRoot });
}
