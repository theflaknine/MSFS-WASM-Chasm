using System.Diagnostics;
using WasmChasm.Core;

namespace WasmChasm.Tests;

/// <summary>Windows-only behaviour. These return early elsewhere (CI runs them on windows-latest).</summary>
public class WindowsTests
{
    [Theory]
    [InlineData("{root}\\MSFS2024\\a.\\work")]
    [InlineData("{root}\\MSFS2024\\a \\work")]
    [InlineData("{root}\\MSFS2024\\a\\work:stream")]
    [InlineData("{root}\\MSFS2024\\a\\work::$DATA")]
    [InlineData("{root}\\MSFS2024\\NOSUCH~1\\work")]
    public void Refuses_windows_specific_tricks(string template)
    {
        if (!OperatingSystem.IsWindows()) return;
        using var f = new Fixture();
        f.AddWasm("MSFS2024", "a");
        var guard = new PathGuard(new[] { f.WasmRoot });
        Assert.False(guard.Check(template.Replace("{root}", f.WasmRoot)).Allowed);
    }

    [Fact]
    public void Path_guard_is_case_insensitive_on_windows()
    {
        if (!OperatingSystem.IsWindows()) return;
        using var f = new Fixture();
        var pkg = f.AddWasm("MSFS2024", "a");
        var guard = new PathGuard(new[] { f.WasmRoot });
        Assert.True(guard.Check(pkg.ToUpperInvariant()).Allowed);
    }

    [Fact]
    public void Junction_work_folder_removes_link_only()
    {
        if (!OperatingSystem.IsWindows()) return;
        using var f = new Fixture();
        var pkg = Path.Combine(f.WasmRoot, "MSFS2024", "linked");
        Directory.CreateDirectory(pkg);
        var target = f.Outside("junction-target");
        var link = Path.Combine(pkg, "work");
        var psi = new ProcessStartInfo("cmd.exe", $"/c mklink /J \"{link}\" \"{target}\"") { CreateNoWindow = true, UseShellExecute = false };
        using (var p = Process.Start(psi)!) p.WaitForExit();
        Assert.True(Directory.Exists(link));

        var svc = new ConsignmentService(Catalog.GuardFor(f.Install), new FakeSim(), null);
        var options = new ConsignOptions();
        var groups = Catalog.Load(f.Install, f.Config);
        Assert.True(groups[0].Entries[0].WorkIsReparsePoint);

        var r = Assert.Single(svc.Execute(ConsignmentService.BuildTargets(groups, options), new PermanentStrategy(), options));
        Assert.True(r.LinkOnly);
        Assert.False(Directory.Exists(link));
        Assert.True(File.Exists(Path.Combine(target, "precious.txt")));
    }

    [Fact]
    public void Locked_file_fails_that_folder_and_continues()
    {
        if (!OperatingSystem.IsWindows()) return;
        using var f = new Fixture();
        var a = f.AddWasm("MSFS2024", "a");
        f.AddWasm("MSFS2024", "b");
        var svc = new ConsignmentService(Catalog.GuardFor(f.Install), new FakeSim(), null);
        var options = new ConsignOptions();
        var targets = ConsignmentService.BuildTargets(Catalog.Load(f.Install, f.Config), options);

        using (File.Open(Path.Combine(a, "work", "sub", "state.bin"), FileMode.Open, FileAccess.Read, FileShare.None))
        {
            var results = svc.Execute(targets, new PermanentStrategy(), options);
            Assert.Equal(ConsignOutcome.Failed, results.Single(r => r.Target.PackageId == "a").Outcome);
            Assert.Equal(ConsignOutcome.Removed, results.Single(r => r.Target.PackageId == "b").Outcome);
        }
    }
}
