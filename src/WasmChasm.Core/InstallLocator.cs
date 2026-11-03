namespace WasmChasm.Core;

/// <summary>One implementation per store: returns the WASM root and UserCfg.opt path, or nothing.</summary>
public interface IInstallLocator
{
    string StoreId { get; }
    InstallLocation? Locate();
}

/// <summary>Store locator driven entirely by a <see cref="StoreDefinition"/> from locations.json.</summary>
public sealed class ConfiguredInstallLocator : IInstallLocator
{
    private readonly StoreDefinition _store;
    private readonly Func<string, string?> _getEnv;

    public ConfiguredInstallLocator(StoreDefinition store, Func<string, string?>? getEnv = null)
    {
        _store = store;
        _getEnv = getEnv ?? Environment.GetEnvironmentVariable;
    }

    public string StoreId => _store.Id;

    public InstallLocation? Locate()
    {
        var root = PathTemplate.Expand(_store.WasmRoot, _getEnv);
        if (root is null || !Directory.Exists(root)) return null;

        // A WASM folder alone doesn't prove an install: add-on tools can create one at the Steam path on a
        // machine that only has the Store version. Where a store defines UserCfg.opt, that file must exist too.
        string? cfg = _store.UserCfg is null ? null : PathTemplate.Expand(_store.UserCfg, _getEnv);
        if (_store.UserCfg is not null && (cfg is null || !File.Exists(cfg))) return null;

        return new InstallLocation(_store.Id, _store.Label, Path.GetFullPath(root), cfg);
    }

    /// <summary>The WASM folder, if it exists but isn't backed by an install. Never cleared unless browsed to.</summary>
    public string? FindStrayRoot()
    {
        var root = PathTemplate.Expand(_store.WasmRoot, _getEnv);
        return root is not null && Directory.Exists(root) && Locate() is null ? Path.GetFullPath(root) : null;
    }
}

public static class InstallDetection
{
    public static IReadOnlyList<IInstallLocator> CreateLocators(LocationsConfig config, Func<string, string?>? getEnv = null) =>
        config.Stores.Select(s => (IInstallLocator)new ConfiguredInstallLocator(s, getEnv)).ToList();

    /// <summary>FR-1: probes every store's WASM root and returns those that exist.</summary>
    public static IReadOnlyList<InstallLocation> DetectAll(IEnumerable<IInstallLocator> locators) =>
        locators.Select(l => l.Locate()).Where(l => l is not null).Select(l => l!).ToList();
}

public static class UserCfgDiscovery
{
    /// <summary>For a custom WASM folder: the first UserCfg.opt any known store has, so titles can still resolve.</summary>
    public static string? FindAny(LocationsConfig config, Func<string, string?>? getEnv = null)
    {
        getEnv ??= Environment.GetEnvironmentVariable;
        foreach (var store in config.Stores)
        {
            if (store.UserCfg is null) continue;
            var path = PathTemplate.Expand(store.UserCfg, getEnv);
            if (path is not null && File.Exists(path)) return path;
        }

        return null;
    }
}
