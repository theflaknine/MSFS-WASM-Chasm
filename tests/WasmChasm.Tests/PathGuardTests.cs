using WasmChasm.Core;

namespace WasmChasm.Tests;

public class PathGuardTests
{
    private static string S(string p) => PathTemplate.Normalise(p);

    [Fact]
    public void Allows_package_and_work_folders_inside_root()
    {
        using var f = new Fixture();
        var pkg = f.AddWasm("MSFS2024", "a");
        var guard = new PathGuard(new[] { f.WasmRoot });
        Assert.True(guard.Check(pkg).Allowed);
        Assert.True(guard.Check(Path.Combine(pkg, "work")).Allowed);
        Assert.Equal(Path.Combine(pkg, "work"), guard.Check(Path.Combine(pkg, "work") + Path.DirectorySeparatorChar).CanonicalPath);
    }

    [Fact]
    public void Refuses_root_and_sim_version_folder()
    {
        using var f = new Fixture();
        f.AddWasm("MSFS2024", "a");
        var guard = new PathGuard(new[] { f.WasmRoot });
        Assert.False(guard.Check(f.WasmRoot).Allowed);
        Assert.False(guard.Check(Path.Combine(f.WasmRoot, "MSFS2024")).Allowed);
    }

    public static IEnumerable<object[]> TraversalStrings()
    {
        // {root} is replaced with the fixture's WASM root.
        yield return new object[] { "{root}/MSFS2024/../../outside/x" };
        yield return new object[] { "{root}/MSFS2024/a/../../../outside" };
        yield return new object[] { "{root}\\MSFS2024\\..\\..\\outside\\x" };
        yield return new object[] { "{root}/MSFS2024\\a/..\\..\\..\\x" };
        yield return new object[] { "{root}/MSFS2024/./a/work" };
        yield return new object[] { "{root}/../WASM/MSFS2024/a" };
        yield return new object[] { "{root}2/MSFS2024/a" };
        yield return new object[] { "{root}_evil/MSFS2024/a" };
        yield return new object[] { "\\\\server\\share\\WASM\\MSFS2024\\a" };
        yield return new object[] { "//server/share/WASM/MSFS2024/a" };
        yield return new object[] { "\\\\?\\C:\\WASM\\MSFS2024\\a" };
        yield return new object[] { "\\\\.\\C:\\WASM\\MSFS2024\\a" };
        yield return new object[] { "MSFS2024/a/work" };
        yield return new object[] { "" };
        yield return new object[] { "   " };
        yield return new object[] { "{root}/MSFS2024/a\0/work" };
        yield return new object[] { "/" };
        yield return new object[] { "C:\\" };
    }

    [Theory]
    [MemberData(nameof(TraversalStrings))]
    public void Refuses_traversal_and_foreign_paths(string template)
    {
        using var f = new Fixture();
        f.AddWasm("MSFS2024", "a");
        f.Outside("x");
        var guard = new PathGuard(new[] { f.WasmRoot });
        var path = template.Replace("{root}", f.WasmRoot);
        Assert.False(guard.Check(path).Allowed, $"Should refuse: {path}");
    }

    [Fact]
    public void Random_fuzz_never_allows_a_path_outside_root()
    {
        using var f = new Fixture();
        f.AddWasm("MSFS2024", "a");
        var guard = new PathGuard(new[] { f.WasmRoot });
        var rng = new Random(1234);
        string[] parts = { "..", ".", "MSFS2024", "a", "work", "\\", "/", "//", "\\\\", "~1", "PROGRA~1", " ", "x.", ":", "outside", "WASM", "C:" };

        for (int i = 0; i < 5000; i++)
        {
            var sb = new System.Text.StringBuilder(rng.Next(2) == 0 ? f.WasmRoot : "");
            int n = rng.Next(1, 8);
            for (int j = 0; j < n; j++)
            {
                sb.Append(rng.Next(3) == 0 ? "\\" : "/");
                sb.Append(parts[rng.Next(parts.Length)]);
            }

            var candidate = sb.ToString();
            var decision = guard.Check(candidate);
            if (!decision.Allowed) continue;

            var full = Path.GetFullPath(decision.CanonicalPath!);
            Assert.StartsWith(f.WasmRoot + Path.DirectorySeparatorChar, full);
            Assert.DoesNotContain("..", candidate.Split('/', '\\'));
        }
    }

    [Fact]
    public void Refuses_target_reached_through_a_linked_ancestor()
    {
        using var f = new Fixture();
        var outside = f.Outside("victim");
        Directory.CreateDirectory(Path.Combine(outside, "work"));
        Directory.CreateDirectory(Path.Combine(f.WasmRoot, "MSFS2024"));
        Fixture.CreateDirectoryLink(Path.Combine(f.WasmRoot, "MSFS2024", "evil"), outside);

        var guard = new PathGuard(new[] { f.WasmRoot });
        Assert.False(guard.Check(Path.Combine(f.WasmRoot, "MSFS2024", "evil", "work")).Allowed);
        // The link itself is allowed as a target: removing it only removes the link.
        Assert.True(guard.Check(Path.Combine(f.WasmRoot, "MSFS2024", "evil")).Allowed);
    }

    [Fact]
    public void Rejects_relative_root()
    {
        Assert.Throws<ArgumentException>(() => new PathGuard(new[] { "relative/root" }));
    }

    [Fact]
    public void Custom_root_anchors_the_guard_to_the_chosen_folder()
    {
        using var f = new Fixture();
        var pkg = f.AddWasm("MSFS2024", "a");
        var guard = Catalog.GuardFor(InstallLocation.Custom(Path.Combine(f.WasmRoot, "MSFS2024")));
        // With the sim-version folder as the root, "a" is depth 1 and must be refused...
        Assert.False(guard.Check(pkg).Allowed);
        // ...and nothing above the chosen folder is reachable.
        Assert.False(guard.Check(Path.Combine(f.WasmRoot, "other", "pkg")).Allowed);
    }
}
