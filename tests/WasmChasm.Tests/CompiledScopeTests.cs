using System.Runtime.Versioning;
using WasmChasm.Core;

namespace WasmChasm.Tests;

public class CompiledScopeTests
{
    private static ConsignmentService Service(Fixture f, FakeRecycleBin? bin = null) =>
        new(Catalog.GuardFor(f.Install), new FakeSim(), null, bin);

    private static string AddPackageWithCache(Fixture f, string package = "pmdg-aircraft-dc6")
    {
        var pkg = f.AddWasm("MSFS2024", package, workBytes: 100);
        f.AddCompiled(pkg);
        return pkg;
    }

    [Fact]
    public void Compiled_cache_is_the_default_everywhere()
    {
        Assert.Equal(DeletionScope.CompiledCache, new ConsignOptions().Scope);
        Assert.Equal(DeletionScope.CompiledCache, new AppSettings().Scope);
        Assert.Equal(DeletionScope.CompiledCache, WasmChasm.Cli.CliOptions.Parse(Array.Empty<string>()).Scope);
    }

    [Fact]
    public void Scanner_recognises_only_top_level_files_with_the_exact_name_shape()
    {
        using var f = new Fixture();
        var pkg = AddPackageWithCache(f);
        foreach (var other in new[] { "module.wasm.cache", "notes.dll", "m123_0.dll", "m78edaad72d8f5958_0.txt", "xm78edaad72d8f5958_0.dll", "m78edaad72d8f5958.dll" })
            File.WriteAllBytes(Path.Combine(pkg, other), new byte[1]);
        // Same name, wrong place: anything inside work is the add-on's own data.
        File.WriteAllBytes(Path.Combine(pkg, "work", Fixture.CompiledNames[1]), new byte[1]);

        var e = Assert.Single(new WasmScanner(f.Config).Scan(f.WasmRoot));

        Assert.Equal(Fixture.CompiledNames.OrderBy(n => n), e.CompiledFiles!.Select(Path.GetFileName).OrderBy(n => n));
        Assert.Equal(60, e.CompiledSizeBytes);
    }

    [Fact]
    public void One_target_per_package_with_the_files_listed_and_a_plain_label()
    {
        using var f = new Fixture();
        var pkg = AddPackageWithCache(f);

        var t = Assert.Single(ConsignmentService.BuildTargets(Catalog.Load(f.Install, f.Config), new ConsignOptions()));

        Assert.Equal(pkg, t.Path);
        Assert.Equal(6, t.Files!.Count);
        Assert.Equal(60, t.SizeBytes);
        Assert.Contains("6 compiled files", t.Label);
        Assert.Contains("work folder kept", t.Label);
    }

    [Fact]
    public void Removes_only_the_compiled_files()
    {
        using var f = new Fixture();
        var pkg = AddPackageWithCache(f);
        File.WriteAllBytes(Path.Combine(pkg, "module.wasm.cache"), new byte[1]);
        var svc = Service(f);
        var options = new ConsignOptions();
        var targets = ConsignmentService.BuildTargets(Catalog.Load(f.Install, f.Config), options);

        var r = Assert.Single(svc.Execute(svc.Preflight(targets, DisposalMode.Permanent).Targets, new PermanentStrategy(), options));

        Assert.Equal(ConsignOutcome.Removed, r.Outcome);
        Assert.All(Fixture.CompiledNames, n => Assert.False(File.Exists(Path.Combine(pkg, n))));
        Assert.True(File.Exists(Path.Combine(pkg, "work", "sub", "state.bin")));
        Assert.True(File.Exists(Path.Combine(pkg, "module.wasm.cache")));
    }

    [Fact]
    public void Recycles_each_file_and_keeps_the_folder()
    {
        using var f = new Fixture();
        var pkg = AddPackageWithCache(f);
        var bin = new FakeRecycleBin();
        var svc = Service(f, bin);
        var options = new ConsignOptions();
        var pre = svc.Preflight(ConsignmentService.BuildTargets(Catalog.Load(f.Install, f.Config), options), DisposalMode.RecycleBin);

        var r = Assert.Single(svc.Execute(pre.Targets, new RecycleBinStrategy(bin), options));

        Assert.Equal(ConsignOutcome.Removed, r.Outcome);
        Assert.Equal(6, bin.Recycled.Count);
        Assert.All(bin.Recycled, p => Assert.Contains("m78edaad72d8f5958_0.", p));
        Assert.True(Directory.Exists(Path.Combine(pkg, "work")));
    }

    [Fact]
    public void Recycle_bin_unavailable_never_falls_back_to_deleting()
    {
        using var f = new Fixture();
        var pkg = AddPackageWithCache(f);
        var bin = new FakeRecycleBin { Unavailable = "Recycle Bin disabled" };
        var svc = Service(f, bin);
        var options = new ConsignOptions();
        var targets = ConsignmentService.BuildTargets(Catalog.Load(f.Install, f.Config), options);

        Assert.Equal(PreflightProblem.RecycleBinUnavailable, svc.Preflight(targets, DisposalMode.RecycleBin).Problem);
        Assert.Equal(ConsignOutcome.Skipped, Assert.Single(svc.Execute(targets, new RecycleBinStrategy(bin), options)).Outcome);
        Assert.All(Fixture.CompiledNames, n => Assert.True(File.Exists(Path.Combine(pkg, n))));
    }

    [Fact]
    public void Dry_run_touches_nothing()
    {
        using var f = new Fixture();
        var pkg = AddPackageWithCache(f);
        var options = new ConsignOptions { DryRun = true };
        var targets = ConsignmentService.BuildTargets(Catalog.Load(f.Install, f.Config), options);

        var r = Assert.Single(Service(f).Execute(targets, new PermanentStrategy(), options));

        Assert.Equal(ConsignOutcome.WouldRemove, r.Outcome);
        Assert.All(Fixture.CompiledNames, n => Assert.True(File.Exists(Path.Combine(pkg, n))));
    }

    [Fact]
    public void Zip_backup_holds_the_compiled_files_only()
    {
        using var f = new Fixture();
        var pkg = AddPackageWithCache(f);
        var options = new ConsignOptions();
        var targets = ConsignmentService.BuildTargets(Catalog.Load(f.Install, f.Config), options);

        var r = Assert.Single(Service(f).Execute(targets, new ZipThenStrategy(Path.Combine(f.Base, "backups"), new PermanentStrategy()), options));

        Assert.Equal(ConsignOutcome.Removed, r.Outcome);
        using var zip = System.IO.Compression.ZipFile.OpenRead(r.BackupPath!);
        Assert.Equal(6, zip.Entries.Count);
        Assert.All(zip.Entries, e => Assert.StartsWith("compiled/m78edaad72d8f5958_0.", e.FullName));
        Assert.True(Directory.Exists(Path.Combine(pkg, "work")));
    }

    [Fact]
    public void Package_with_no_cache_has_nothing_to_consign()
    {
        using var f = new Fixture();
        f.AddWasm("MSFS2024", "a");
        var svc = Service(f);
        var targets = ConsignmentService.BuildTargets(Catalog.Load(f.Install, f.Config), new ConsignOptions());

        Assert.Empty(targets);
        Assert.Equal(PreflightProblem.NothingToConsign, svc.Preflight(targets, DisposalMode.Permanent).Problem);
    }

    [Fact]
    public void Forged_file_outside_the_root_refuses_the_whole_target()
    {
        using var f = new Fixture();
        var pkg = AddPackageWithCache(f);
        var victim = Path.Combine(f.Outside("victim"), "precious.txt");
        var svc = Service(f);
        var files = new[] { Path.Combine(pkg, Fixture.CompiledNames[0]), victim };
        var forged = new[] { new ConsignTarget("x", "X", "MSFS2024", pkg, 0, files) };

        Assert.Single(svc.Preflight(forged, DisposalMode.Permanent).Refused);
        var r = Assert.Single(svc.Execute(forged, new PermanentStrategy(), new ConsignOptions()));

        Assert.Equal(ConsignOutcome.Skipped, r.Outcome);
        Assert.True(File.Exists(victim));
        Assert.True(File.Exists(files[0]));
    }

    [Fact]
    public void Package_folder_swapped_for_a_link_after_the_scan_is_refused()
    {
        using var f = new Fixture();
        var pkg = AddPackageWithCache(f);
        var options = new ConsignOptions();
        var targets = ConsignmentService.BuildTargets(Catalog.Load(f.Install, f.Config), options);

        var outside = f.Outside("swap");
        f.AddCompiled(outside);
        Directory.Delete(pkg, recursive: true);
        Fixture.CreateDirectoryLink(pkg, outside);

        var r = Assert.Single(Service(f).Execute(targets, new PermanentStrategy(), options));

        Assert.Equal(ConsignOutcome.Skipped, r.Outcome);
        Assert.All(Fixture.CompiledNames, n => Assert.True(File.Exists(Path.Combine(outside, n))));
    }

    [Fact]
    public void Linked_package_folder_has_no_compiled_files()
    {
        using var f = new Fixture();
        var outside = f.Outside("linked-package");
        f.AddCompiled(outside);
        Directory.CreateDirectory(Path.Combine(f.WasmRoot, "MSFS2024"));
        Fixture.CreateDirectoryLink(Path.Combine(f.WasmRoot, "MSFS2024", "linked"), outside);

        var e = Assert.Single(new WasmScanner(f.Config).Scan(f.WasmRoot));

        Assert.False(e.HasCompiledCache);
    }

    [Fact]
    public void Files_gone_since_the_scan_are_skipped()
    {
        using var f = new Fixture();
        var pkg = AddPackageWithCache(f);
        var options = new ConsignOptions();
        var targets = ConsignmentService.BuildTargets(Catalog.Load(f.Install, f.Config), options);
        foreach (var n in Fixture.CompiledNames) File.Delete(Path.Combine(pkg, n));

        var r = Assert.Single(Service(f).Execute(targets, new PermanentStrategy(), options));

        Assert.Equal(ConsignOutcome.Skipped, r.Outcome);
        Assert.Contains("no longer exist", r.Reason);
    }

    [Fact]
    public void Invalid_pattern_in_a_hand_edited_config_matches_nothing()
    {
        var cfg = new LocationsConfig { CompiledCachePattern = "(unclosed" };
        Assert.False(cfg.IsCompiledCacheFile(Fixture.CompiledNames[0]));
    }

    [Fact]
    public void Embedded_pattern_matches_the_default()
    {
        Assert.Equal(LocationsConfig.DefaultCompiledCachePattern, LocationsConfig.LoadEmbedded().CompiledCachePattern);
    }

    [Theory]
    [InlineData(DeletionScope.WorkFolder)]
    [InlineData(DeletionScope.PackageFolder)]
    public void Destructive_scopes_warn_about_licence_files(DeletionScope scope)
    {
        Assert.Contains("licence", ScopeText.Warning(scope));
    }

    [Fact]
    public void Cli_defaults_to_compiled_and_says_the_work_folder_is_kept()
    {
        using var f = new Fixture();
        var pkg = AddPackageWithCache(f);
        var o = new StringWriter();

        Assert.Equal(0, WasmChasm.Cli.Program.Run(new[] { "purge", "pmdg-aircraft-dc6", "--root", f.WasmRoot, "--dry-run" }, o, o, f.Config));

        Assert.Contains("COMPILED CACHE ONLY", o.ToString());
        Assert.Contains("work folder kept", o.ToString());
        Assert.All(Fixture.CompiledNames, n => Assert.True(File.Exists(Path.Combine(pkg, n))));
    }
}

public class ResultsPageTests
{
    private static readonly ConsignTarget Target = new("a", "A", "MSFS2024", "target", 10);

    [Fact]
    public void A_clean_purge_needs_no_page()
    {
        var results = new[] { new ConsignResult(Target, ConsignOutcome.Removed, "ok"), new ConsignResult(Target, ConsignOutcome.Removed, "ok") };
        Assert.False(ScopeText.NeedsResultsPage(results));
    }

    [Theory]
    [InlineData(ConsignOutcome.Failed)]
    [InlineData(ConsignOutcome.Skipped)]
    [InlineData(ConsignOutcome.WouldRemove)]
    public void Anything_but_a_clean_removal_needs_the_page(ConsignOutcome outcome)
    {
        var results = new[] { new ConsignResult(Target, ConsignOutcome.Removed, "ok"), new ConsignResult(Target, outcome, "why") };
        Assert.True(ScopeText.NeedsResultsPage(results));
    }

    [Fact]
    public void A_removed_link_needs_the_page()
    {
        Assert.True(ScopeText.NeedsResultsPage(new[] { new ConsignResult(Target, ConsignOutcome.Removed, "ok", LinkOnly: true) }));
    }

    [Fact]
    public void A_backup_needs_the_page_so_its_location_is_shown()
    {
        Assert.True(ScopeText.NeedsResultsPage(new[] { new ConsignResult(Target, ConsignOutcome.Removed, "ok", BackupPath: "backup.zip") }));
    }
}

public class ModeSettingTests
{
    [Fact]
    public void Retro_is_the_default_and_the_choice_is_remembered()
    {
        using var f = new Fixture();
        var path = Path.Combine(f.Base, "settings.json");
        Assert.Equal(AppMode.Retro, AppSettings.Load(path).Mode);

        new AppSettings { Mode = AppMode.Plain }.Save(path);

        Assert.Equal(AppMode.Plain, AppSettings.Load(path).Mode);
    }

    [Fact]
    public void Older_settings_without_a_mode_load_as_retro()
    {
        using var f = new Fixture();
        var path = Path.Combine(f.Base, "settings.json");
        File.WriteAllText(path, "{ \"Sound\": true }");

        var s = AppSettings.Load(path);

        Assert.Equal(AppMode.Retro, s.Mode);
        Assert.True(s.Sound);
    }
}

public class StrayInstallTests
{
    private static StoreDefinition Steam => new() { Id = "steam", Label = "STEAM", WasmRoot = "%R%\\AppData\\WASM", UserCfg = "%R%\\AppData\\UserCfg.opt" };

    [Fact]
    public void Wasm_folder_without_usercfg_is_not_an_install()
    {
        using var f = new Fixture();
        File.Delete(f.UserCfg);
        var locator = new ConfiguredInstallLocator(Steam, n => n == "R" ? f.Base : null);

        Assert.Null(locator.Locate());
        Assert.Equal(f.WasmRoot, locator.FindStrayRoot());
    }

    [Fact]
    public void Real_install_is_not_reported_as_stray()
    {
        using var f = new Fixture();
        var locator = new ConfiguredInstallLocator(Steam, n => n == "R" ? f.Base : null);

        Assert.NotNull(locator.Locate());
        Assert.Null(locator.FindStrayRoot());
    }

    [Fact]
    public void Store_only_machine_with_a_stray_steam_folder_detects_only_the_store()
    {
        using var f = new Fixture();
        // AppData\WASM stands in for the Steam path, with no UserCfg.opt beside it.
        File.Delete(f.UserCfg);
        Directory.CreateDirectory(Path.Combine(f.Base, "Local", "WASM"));
        File.WriteAllText(Path.Combine(f.Base, "Local", "UserCfg.opt"), "Version 1\n");
        var cfg = new LocationsConfig
        {
            Stores =
            {
                Steam,
                new StoreDefinition { Id = "store", Label = "STORE", WasmRoot = "%R%\\Local\\WASM", UserCfg = "%R%\\Local\\UserCfg.opt" },
            },
        };

        var found = InstallDetection.DetectAll(InstallDetection.CreateLocators(cfg, n => n == "R" ? f.Base : null));

        Assert.Equal("store", Assert.Single(found).StoreId);
    }
}

public class LibraryLookupTests
{
    [Theory]
    [InlineData("Community2024")]
    [InlineData("Official2024/OneStore")]
    [InlineData("Official2020/OneStore")]
    public void Finds_titles_in_every_library_folder(string where)
    {
        using var f = new Fixture();
        f.AddManifest(PathTemplate.Normalise(where), "ctl-aircraft-fa50", "Dassault Falcon 50");
        var r = new ManifestResolver(f.Packages, f.Config.ManifestSearch);

        Assert.Equal("Dassault Falcon 50", r.ResolveTitle("ctl-aircraft-fa50"));
    }

    [Fact]
    public void Package_with_no_title_shows_its_package_id()
    {
        using var f = new Fixture();
        f.AddWasm("MSFS2024", "pmdg-aircraft-dc6");
        Assert.Equal("pmdg-aircraft-dc6", Assert.Single(Catalog.Load(f.Install, f.Config)).DisplayName);
    }

    [Fact]
    public void Summary_counts_aircraft_not_folders()
    {
        using var f = new Fixture();
        f.AddWasm("MSFS2024", "a");
        f.AddWasm("MSFS2020", "a");
        var options = new ConsignOptions { Scope = DeletionScope.WorkFolder, DryRun = true };
        var targets = ConsignmentService.BuildTargets(Catalog.Load(f.Install, f.Config), options);
        var svc = new ConsignmentService(Catalog.GuardFor(f.Install), new FakeSim(), null);

        var results = svc.Execute(targets, new PermanentStrategy(), options);

        Assert.Equal(2, results.Count);
        Assert.StartsWith("DRY RUN DONE. 1 AIRCRAFT WOULD BE PURGED.", ScopeText.Summary(results, dryRun: true));
    }

    [Fact]
    public void Linked_community_package_gets_its_title()
    {
        using var f = new Fixture();
        f.AddWasm("MSFS2024", "pmdg-aircraft-dc6");
        var outside = f.Outside("addon-store");
        File.WriteAllText(Path.Combine(outside, "manifest.json"), "{ \"title\": \"PMDG DC-6\" }");
        Directory.CreateDirectory(Path.Combine(f.Packages, "Community"));
        Fixture.CreateDirectoryLink(Path.Combine(f.Packages, "Community", "pmdg-aircraft-dc6"), outside);

        var g = Assert.Single(Catalog.Load(f.Install, f.Config));

        Assert.Equal("PMDG DC-6", g.Title);
    }
}

/// <summary>The Store's LocalState is a junction (seen pointing at another drive), above the WASM root.</summary>
public class JunctionAboveRootTests
{
    private static InstallLocation LinkedInstall(Fixture f)
    {
        var real = f.Outside("wpsystem-localstate");
        Directory.CreateDirectory(Path.Combine(real, "WASM"));
        var localState = Path.Combine(f.Base, "LocalState");
        Fixture.CreateDirectoryLink(localState, real);
        return new InstallLocation("store", "STORE", Path.Combine(localState, "WASM"), f.UserCfg);
    }

    private static string AddPackage(string wasmRoot)
    {
        var pkg = Path.Combine(wasmRoot, "MSFS2024", "a");
        Directory.CreateDirectory(Path.Combine(pkg, "work"));
        File.WriteAllText(Path.Combine(pkg, "work", "settings.ini"), "keep");
        foreach (var n in Fixture.CompiledNames) File.WriteAllBytes(Path.Combine(pkg, n), new byte[10]);
        return pkg;
    }

    [Theory]
    [InlineData(DeletionScope.CompiledCache)]
    [InlineData(DeletionScope.WorkFolder)]
    public void Scope_works_through_a_junction_above_the_root(DeletionScope scope)
    {
        using var f = new Fixture();
        var install = LinkedInstall(f);
        var pkg = AddPackage(install.WasmRoot);
        var svc = new ConsignmentService(Catalog.GuardFor(install), new FakeSim(), null);
        var options = new ConsignOptions { Scope = scope };
        var groups = Catalog.Load(install, f.Config);

        var pre = svc.Preflight(ConsignmentService.BuildTargets(groups, options), DisposalMode.Permanent);
        Assert.Empty(pre.Refused);
        var r = Assert.Single(svc.Execute(pre.Targets, new PermanentStrategy(), options));

        Assert.Equal(ConsignOutcome.Removed, r.Outcome);
        Assert.Equal(scope == DeletionScope.WorkFolder, !Directory.Exists(Path.Combine(pkg, "work")));
        Assert.Equal(scope == DeletionScope.WorkFolder, File.Exists(Path.Combine(pkg, Fixture.CompiledNames[0])));
    }

    /// <summary>
    /// Uses the real Recycle Bin, so it only runs when asked: set WASMCHASM_RECYCLE_TEST to a scratch folder,
    /// ideally on another drive to mirror a Store install moved off C:. It leaves small files in the bin.
    /// </summary>
    [Fact]
    public void Real_recycle_bin_through_a_junction()
    {
        var scratch = Environment.GetEnvironmentVariable("WASMCHASM_RECYCLE_TEST");
        if (string.IsNullOrEmpty(scratch) || !OperatingSystem.IsWindows()) return;
        RunRealRecycle(scratch);
    }

    [SupportedOSPlatform("windows")]
    private static void RunRealRecycle(string scratch)
    {
        using var f = new Fixture();
        var real = Path.Combine(scratch, "wasmchasm-recycle-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(Path.Combine(real, "WASM"));
        var localState = Path.Combine(f.Base, "LocalState");
        Fixture.CreateDirectoryLink(localState, real);
        var install = new InstallLocation("store", "STORE", Path.Combine(localState, "WASM"), f.UserCfg);
        var pkg = AddPackage(install.WasmRoot);
        Assert.Equal(Path.GetPathRoot(Path.GetFullPath(scratch)), WindowsRecycleBin.PhysicalDriveRoot(Path.Combine(pkg, Fixture.CompiledNames[0])), ignoreCase: true);
        var bin = new WindowsRecycleBin();
        var svc = new ConsignmentService(Catalog.GuardFor(install), new FakeSim(), null, bin);
        var options = new ConsignOptions();

        var pre = svc.Preflight(ConsignmentService.BuildTargets(Catalog.Load(install, f.Config), options), DisposalMode.RecycleBin);
        Assert.Equal(PreflightProblem.None, pre.Problem);
        var r = Assert.Single(svc.Execute(pre.Targets, new RecycleBinStrategy(bin), options));

        Assert.True(r.Outcome == ConsignOutcome.Removed, r.Reason);
        Assert.All(Fixture.CompiledNames, n => Assert.False(File.Exists(Path.Combine(pkg, n))));
        Assert.True(File.Exists(Path.Combine(real, "WASM", "MSFS2024", "a", "work", "settings.ini")));
        SafeDelete.DeleteTree(real);
    }
}
