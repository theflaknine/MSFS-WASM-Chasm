using WasmChasm.Core;

namespace WasmChasm.Tests;

public class ConsignmentTests
{
    private static (ConsignmentService svc, FakeSim sim, FakeRecycleBin bin, SessionLog log) Service(Fixture f)
    {
        var sim = new FakeSim();
        var bin = new FakeRecycleBin();
        var log = new SessionLog(Path.Combine(f.Base, "log", "wasmchasm.log"));
        return (new ConsignmentService(Catalog.GuardFor(f.Install), sim, log, bin), sim, bin, log);
    }

    [Fact]
    public void Default_scope_targets_work_folder_only_and_excludes_unknown_siblings()
    {
        using var f = new Fixture();
        f.AddWasm("MSFS2024", "a");
        f.AddWasm("MSFS2020", "a");
        f.AddWasm("MSFS2024_now_valid", "a");
        var groups = Catalog.Load(f.Install, f.Config);

        var targets = ConsignmentService.BuildTargets(groups, new ConsignOptions());

        Assert.Equal(2, targets.Count);
        Assert.All(targets, t => Assert.Equal("work", Path.GetFileName(t.Path)));
        Assert.DoesNotContain(targets, t => t.SimVersionFolder == "MSFS2024_now_valid");

        var withUnknown = ConsignmentService.BuildTargets(groups, new ConsignOptions { IncludeUnknownSimVersions = true });
        Assert.Equal(3, withUnknown.Count);

        var whole = ConsignmentService.BuildTargets(groups, new ConsignOptions { Scope = DeletionScope.PackageFolder });
        Assert.All(whole, t => Assert.Equal("a", Path.GetFileName(t.Path)));
    }

    [Fact]
    public void Dry_run_touches_nothing()
    {
        using var f = new Fixture();
        var pkg = f.AddWasm("MSFS2024", "a");
        var (svc, _, bin, _) = Service(f);
        var options = new ConsignOptions { DryRun = true };
        var targets = ConsignmentService.BuildTargets(Catalog.Load(f.Install, f.Config), options);

        var results = svc.Execute(svc.Preflight(targets, DisposalMode.RecycleBin).Targets, new RecycleBinStrategy(bin), options);

        Assert.Equal(ConsignOutcome.WouldRemove, Assert.Single(results).Outcome);
        Assert.True(Directory.Exists(Path.Combine(pkg, "work")));
        Assert.Empty(bin.Recycled);
    }

    [Fact]
    public void Recycles_work_folder_and_leaves_package_folder()
    {
        using var f = new Fixture();
        var pkg = f.AddWasm("MSFS2024", "a", otherBytes: 10);
        var (svc, _, bin, log) = Service(f);
        var options = new ConsignOptions();
        var targets = ConsignmentService.BuildTargets(Catalog.Load(f.Install, f.Config), options);
        var progress = new List<ConsignProgress>();

        var results = svc.Execute(svc.Preflight(targets, DisposalMode.RecycleBin).Targets, new RecycleBinStrategy(bin), options,
            new SyncProgress<ConsignProgress>(progress.Add));

        Assert.Equal(ConsignOutcome.Removed, Assert.Single(results).Outcome);
        Assert.False(Directory.Exists(Path.Combine(pkg, "work")));
        Assert.True(File.Exists(Path.Combine(pkg, "module.wasm.cache")));
        Assert.Single(bin.Recycled);
        Assert.Single(progress);
        Assert.Contains("REMOVED", File.ReadAllText(log.FilePath).ToUpperInvariant());
    }

    [Fact]
    public void Preflight_blocks_when_sim_running()
    {
        using var f = new Fixture();
        f.AddWasm("MSFS2024", "a");
        var (svc, sim, _, _) = Service(f);
        sim.RunningAfterCalls = 0;
        var targets = ConsignmentService.BuildTargets(Catalog.Load(f.Install, f.Config), new ConsignOptions());
        Assert.Equal(PreflightProblem.SimRunning, svc.Preflight(targets, DisposalMode.RecycleBin).Problem);
    }

    [Fact]
    public void Sim_launching_mid_consign_skips_the_rest()
    {
        using var f = new Fixture();
        f.AddWasm("MSFS2024", "a");
        f.AddWasm("MSFS2024", "b");
        f.AddWasm("MSFS2024", "c");
        var (svc, sim, bin, _) = Service(f);
        var options = new ConsignOptions();
        var targets = ConsignmentService.BuildTargets(Catalog.Load(f.Install, f.Config), options);
        var pre = svc.Preflight(targets, DisposalMode.RecycleBin);
        sim.RunningAfterCalls = sim.Calls + 1; // first folder OK, then the sim appears

        var results = svc.Execute(pre.Targets, new RecycleBinStrategy(bin), options);

        Assert.Equal(new[] { ConsignOutcome.Removed, ConsignOutcome.Skipped, ConsignOutcome.Skipped }, results.Select(r => r.Outcome));
        Assert.All(results.Skip(1), r => Assert.Contains("Simulator", r.Reason));
    }

    [Fact]
    public void Preflight_reports_recycle_bin_unavailable_and_nothing_is_deleted()
    {
        using var f = new Fixture();
        var pkg = f.AddWasm("MSFS2024", "a");
        var (svc, _, bin, _) = Service(f);
        bin.Unavailable = "Recycle Bin disabled";
        var targets = ConsignmentService.BuildTargets(Catalog.Load(f.Install, f.Config), new ConsignOptions());

        var pre = svc.Preflight(targets, DisposalMode.RecycleBin);
        Assert.Equal(PreflightProblem.RecycleBinUnavailable, pre.Problem);

        // Even if a caller ignored preflight, the strategy never silently deletes.
        var results = svc.Execute(pre.Targets, new RecycleBinStrategy(bin), new ConsignOptions());
        Assert.Equal(ConsignOutcome.Skipped, Assert.Single(results).Outcome);
        Assert.True(Directory.Exists(Path.Combine(pkg, "work")));
    }

    [Fact]
    public void Zip_backup_then_remove()
    {
        using var f = new Fixture();
        var pkg = f.AddWasm("MSFS2024", "a", workBytes: 2048);
        var backups = Path.Combine(f.Base, "backups");
        var (svc, _, bin, _) = Service(f);
        var options = new ConsignOptions();
        var targets = ConsignmentService.BuildTargets(Catalog.Load(f.Install, f.Config), options);

        var results = svc.Execute(targets, new ZipThenStrategy(backups, new RecycleBinStrategy(bin)), options);

        var r = Assert.Single(results);
        Assert.Equal(ConsignOutcome.Removed, r.Outcome);
        Assert.NotNull(r.BackupPath);
        Assert.True(File.Exists(r.BackupPath));
        using (var zip = System.IO.Compression.ZipFile.OpenRead(r.BackupPath!))
            Assert.Contains(zip.Entries, e => e.FullName == "work/sub/state.bin" && e.Length == 2048);
        Assert.False(Directory.Exists(Path.Combine(pkg, "work")));
    }

    [Fact]
    public void Junction_work_folder_removes_link_only()
    {
        using var f = new Fixture();
        var pkg = Path.Combine(f.WasmRoot, "MSFS2024", "linked");
        Directory.CreateDirectory(pkg);
        var target = f.Outside("addons-linker-target");
        Directory.CreateSymbolicLink(Path.Combine(pkg, "work"), target);
        var (svc, _, _, _) = Service(f);
        var options = new ConsignOptions();
        var targets = ConsignmentService.BuildTargets(Catalog.Load(f.Install, f.Config), options);

        var results = svc.Execute(svc.Preflight(targets, DisposalMode.Permanent).Targets, new PermanentStrategy(), options);

        var r = Assert.Single(results);
        Assert.Equal(ConsignOutcome.Removed, r.Outcome);
        Assert.True(r.LinkOnly);
        Assert.False(Directory.Exists(Path.Combine(pkg, "work")));
        Assert.True(File.Exists(Path.Combine(target, "precious.txt")));
    }

    [Fact]
    public void Permanent_delete_does_not_follow_nested_links()
    {
        using var f = new Fixture();
        var pkg = f.AddWasm("MSFS2024", "a");
        var outside = f.Outside("nested-target");
        Directory.CreateSymbolicLink(Path.Combine(pkg, "work", "sub", "navdata"), outside);
        var (svc, _, _, _) = Service(f);
        var options = new ConsignOptions();
        var targets = ConsignmentService.BuildTargets(Catalog.Load(f.Install, f.Config), options);

        svc.Execute(targets, new PermanentStrategy(), options);

        Assert.False(Directory.Exists(Path.Combine(pkg, "work")));
        Assert.True(File.Exists(Path.Combine(outside, "precious.txt")));
    }

    [Fact]
    public void Path_guard_refuses_forged_targets_outside_root()
    {
        using var f = new Fixture();
        var outside = f.Outside("victim");
        var (svc, _, _, _) = Service(f);
        var forged = new[] { new ConsignTarget("x", "X", "MSFS2024", outside, 0) };

        var pre = svc.Preflight(forged, DisposalMode.Permanent);
        Assert.Empty(pre.Targets);
        Assert.Single(pre.Refused);

        var results = svc.Execute(forged, new PermanentStrategy(), new ConsignOptions());
        Assert.Equal(ConsignOutcome.Skipped, Assert.Single(results).Outcome);
        Assert.True(File.Exists(Path.Combine(outside, "precious.txt")));
    }

    [Fact]
    public void Long_paths_are_handled()
    {
        using var f = new Fixture();
        var pkg = f.AddWasm("MSFS2024", "long");
        var deep = Path.Combine(pkg, "work");
        while (deep.Length < 400) deep = Path.Combine(deep, new string('d', 40));
        Directory.CreateDirectory(deep);
        File.WriteAllText(Path.Combine(deep, "f.txt"), "x");
        var (svc, _, _, _) = Service(f);
        var options = new ConsignOptions();
        var groups = Catalog.Load(f.Install, f.Config);
        Assert.True(groups[0].WorkSizeBytes > 100);

        var results = svc.Execute(ConsignmentService.BuildTargets(groups, options), new PermanentStrategy(), options);

        Assert.Equal(ConsignOutcome.Removed, Assert.Single(results).Outcome);
        Assert.False(Directory.Exists(Path.Combine(pkg, "work")));
    }

    [Fact]
    public void Missing_folder_is_skipped_with_reason()
    {
        using var f = new Fixture();
        var pkg = f.AddWasm("MSFS2024", "a");
        var (svc, _, _, _) = Service(f);
        var options = new ConsignOptions();
        var targets = ConsignmentService.BuildTargets(Catalog.Load(f.Install, f.Config), options);
        Directory.Delete(Path.Combine(pkg, "work"), true);

        var r = Assert.Single(svc.Execute(targets, new PermanentStrategy(), options));
        Assert.Equal(ConsignOutcome.Skipped, r.Outcome);
        Assert.Contains("no longer exists", r.Reason);
    }

    /// <summary>Progress&lt;T&gt; posts asynchronously; tests need synchronous delivery.</summary>
    private sealed class SyncProgress<T> : IProgress<T>
    {
        private readonly Action<T> _a;
        public SyncProgress(Action<T> a) => _a = a;
        public void Report(T value) => _a(value);
    }
}

public class CliTests
{
    [Fact]
    public void Scan_and_dry_run_consign_via_cli()
    {
        using var f = new Fixture();
        var pkg = f.AddWasm("MSFS2024", "pmdg-aircraft-77w", workBytes: 3000);
        f.AddManifest("Community", "pmdg-aircraft-77w", "PMDG 777-300ER");

        var o = new StringWriter();
        Assert.Equal(0, WasmChasm.Cli.Program.Run(new[] { "scan", "--root", f.WasmRoot }, o, o, f.Config));
        Assert.Contains("pmdg-aircraft-77w", o.ToString());

        o = new StringWriter();
        Assert.Equal(0, WasmChasm.Cli.Program.Run(new[] { "scan", "--root", f.WasmRoot, "--json" }, o, o, f.Config));
        Assert.Contains("\"packageId\": \"pmdg-aircraft-77w\"", o.ToString());

        o = new StringWriter();
        Assert.Equal(0, WasmChasm.Cli.Program.Run(new[] { "consign", "pmdg-aircraft-77w", "--root", f.WasmRoot, "--dry-run" }, o, o, f.Config));
        Assert.Contains("DRY RUN COMPLETE. 1 RECORDS WOULD BE RELEASED", o.ToString());
        Assert.True(Directory.Exists(Path.Combine(pkg, "work")));
    }

    [Fact]
    public void Consign_without_yes_is_a_dry_run()
    {
        using var f = new Fixture();
        var pkg = f.AddWasm("MSFS2024", "a");
        var o = new StringWriter();
        WasmChasm.Cli.Program.Run(new[] { "consign", "a", "--root", f.WasmRoot, "--mode", "permanent" }, o, o, f.Config);
        Assert.True(Directory.Exists(Path.Combine(pkg, "work")));
        Assert.Contains("DRY RUN", o.ToString());
    }
}
