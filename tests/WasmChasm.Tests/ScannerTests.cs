using WasmChasm.Core;

namespace WasmChasm.Tests;

public class ScannerTests
{
    [Fact]
    public void Scans_every_sim_version_folder_including_unknown_siblings()
    {
        using var f = new Fixture();
        f.AddWasm("MSFS2024", "pmdg-aircraft-77w", workBytes: 1000);
        f.AddWasm("MSFS2020", "fenix-a320", workBytes: 500);
        f.AddWasm("MSFS2024_now_valid", "pmdg-aircraft-77w", workBytes: 10);

        var entries = new WasmScanner(f.Config).Scan(f.WasmRoot);

        Assert.Equal(3, entries.Count);
        var unknown = Assert.Single(entries, e => e.SimVersionFolder == "MSFS2024_now_valid");
        Assert.False(unknown.IsKnownSimVersion);
        var known = Assert.Single(entries, e => e.SimVersionFolder == "MSFS2024");
        Assert.True(known.IsKnownSimVersion);
        Assert.Equal(1000, known.WorkSizeBytes);
        Assert.True(known.HasWorkFolder);
    }

    [Fact]
    public void Package_size_includes_files_outside_work()
    {
        using var f = new Fixture();
        f.AddWasm("MSFS2024", "a", workBytes: 100, otherBytes: 50);
        var e = Assert.Single(new WasmScanner(f.Config).Scan(f.WasmRoot));
        Assert.Equal(150, e.SizeBytes);
        Assert.Equal(100, e.WorkSizeBytes);
    }

    [Fact]
    public void Missing_root_returns_nothing()
    {
        var cfg = LocationsConfig.LoadEmbedded();
        Assert.Empty(new WasmScanner(cfg).Scan(Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString())));
    }

    [Fact]
    public void Groups_same_package_across_siblings_and_resolves_titles()
    {
        using var f = new Fixture();
        f.AddWasm("MSFS2024", "pmdg-aircraft-77w");
        f.AddWasm("MSFS2020", "pmdg-aircraft-77w");
        f.AddWasm("MSFS2024", "gone-aircraft");
        f.AddManifest("Community", "pmdg-aircraft-77w", "PMDG 777-300ER");

        var groups = Catalog.Load(f.Install, f.Config);

        Assert.Equal(2, groups.Count);
        var pmdg = Assert.Single(groups, g => g.PackageId == "pmdg-aircraft-77w");
        Assert.Equal("PMDG 777-300ER", pmdg.Title);
        Assert.Equal(2, pmdg.Entries.Count);
        var orphan = Assert.Single(groups, g => g.PackageId == "gone-aircraft");
        Assert.True(orphan.IsOrphan);
        Assert.Equal("gone-aircraft", orphan.DisplayName);
    }

    [Fact]
    public void Junction_work_folder_is_flagged_and_not_measured()
    {
        using var f = new Fixture();
        var pkg = Path.Combine(f.WasmRoot, "MSFS2024", "linked");
        Directory.CreateDirectory(pkg);
        var target = f.Outside("linked-target");
        File.WriteAllBytes(Path.Combine(target, "big.bin"), new byte[4096]);
        Directory.CreateSymbolicLink(Path.Combine(pkg, "work"), target);

        var e = Assert.Single(new WasmScanner(f.Config).Scan(f.WasmRoot));
        Assert.True(e.HasWorkFolder);
        Assert.True(e.WorkIsReparsePoint);
        Assert.Equal(0, e.WorkSizeBytes);
        Assert.Equal(0, e.SizeBytes);
    }
}

public class ManifestTests
{
    [Theory]
    [InlineData("InstalledPackagesPath \"D:\\MSFS\\Packages\"", "D:\\MSFS\\Packages")]
    [InlineData("Foo 1\r\nInstalledPackagesPath   \"C:\\Users\\a b\\Packages\"\r\nBar 2", "C:\\Users\\a b\\Packages")]
    [InlineData("InstalledPackagesPath E:\\Pkgs", "E:\\Pkgs")]
    public void Parses_installed_packages_path(string cfg, string expected)
    {
        Assert.Equal(PathTemplate.Normalise(expected), UserCfgParser.ParseInstalledPackagesPath(cfg));
    }

    [Fact]
    public void Missing_installed_packages_path_returns_null()
    {
        Assert.Null(UserCfgParser.ParseInstalledPackagesPath("Version 1\n"));
    }

    [Theory]
    [InlineData("Community")]
    [InlineData("Official/OneStore")]
    [InlineData("Official/Steam")]
    [InlineData("StreamedPackages")]
    public void Resolves_from_each_package_location(string where)
    {
        using var f = new Fixture();
        f.AddManifest(PathTemplate.Normalise(where), "asobo-aircraft-c172", "Cessna 172");
        var r = new ManifestResolver(f.Packages, f.Config.ManifestSearch);
        Assert.Equal("Cessna 172", r.ResolveTitle("asobo-aircraft-c172"));
    }

    [Fact]
    public void Bad_manifest_or_traversal_resolves_to_null()
    {
        using var f = new Fixture();
        var dir = Path.Combine(f.Packages, "Community", "broken");
        Directory.CreateDirectory(dir);
        File.WriteAllText(Path.Combine(dir, "manifest.json"), "{ not json");
        var r = new ManifestResolver(f.Packages, f.Config.ManifestSearch);
        Assert.Null(r.ResolveTitle("broken"));
        Assert.Null(r.ResolveTitle(".."));
        Assert.Null(r.ResolveTitle("does-not-exist"));
    }

    [Fact]
    public void No_usercfg_means_every_entry_is_orphaned()
    {
        using var f = new Fixture();
        f.AddWasm("MSFS2024", "x");
        var groups = Catalog.Load(f.Install with { UserCfgPath = null }, f.Config);
        Assert.True(Assert.Single(groups).IsOrphan);
    }
}

public class LocationsTests
{
    [Fact]
    public void Embedded_config_has_both_stores()
    {
        var cfg = LocationsConfig.LoadEmbedded();
        Assert.Contains(cfg.Stores, s => s.Id == "steam");
        Assert.Contains(cfg.Stores, s => s.Id == "store");
        Assert.Contains("FlightSimulator2024", cfg.SimProcessNames);
        Assert.True(cfg.IsKnownSimVersion("msfs2024"));
        Assert.False(cfg.IsKnownSimVersion("MSFS2024_now_valid"));
    }

    [Fact]
    public void Expands_environment_variables()
    {
        var path = PathTemplate.Expand("%APPDATA%\\Microsoft Flight Simulator 2024\\WASM", n => n == "APPDATA" ? "/home/x/AppData" : null);
        Assert.Equal(PathTemplate.Normalise("/home/x/AppData/Microsoft Flight Simulator 2024/WASM"), path);
        Assert.Null(PathTemplate.Expand("%NOPE%\\x", _ => null));
    }

    [Fact]
    public void Locator_finds_install_only_when_root_exists()
    {
        using var f = new Fixture();
        var store = new StoreDefinition { Id = "steam", Label = "STEAM", WasmRoot = "%ROOT%\\AppData\\WASM", UserCfg = "%ROOT%\\AppData\\UserCfg.opt" };
        var found = new ConfiguredInstallLocator(store, n => n == "ROOT" ? f.Base : null).Locate();
        Assert.NotNull(found);
        Assert.Equal(f.WasmRoot, found!.WasmRoot);
        Assert.Equal(f.UserCfg, found.UserCfgPath);

        var missing = new ConfiguredInstallLocator(store, n => n == "ROOT" ? Path.Combine(f.Base, "nope") : null).Locate();
        Assert.Null(missing);
    }

    [Fact]
    public void Detects_both_stores_on_one_machine()
    {
        using var f = new Fixture();
        Directory.CreateDirectory(Path.Combine(f.Base, "Local", "WASM"));
        var cfg = new LocationsConfig
        {
            Stores =
            {
                new StoreDefinition { Id = "steam", WasmRoot = "%B%\\AppData\\WASM" },
                new StoreDefinition { Id = "store", WasmRoot = "%B%\\Local\\WASM" },
            },
        };
        var all = InstallDetection.DetectAll(InstallDetection.CreateLocators(cfg, n => n == "B" ? f.Base : null));
        Assert.Equal(new[] { "steam", "store" }, all.Select(a => a.StoreId));
    }
}
