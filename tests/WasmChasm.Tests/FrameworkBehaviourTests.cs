using WasmChasm.Core;

namespace WasmChasm.Tests;

/// <summary>File system behaviour that the safety rules depend on and that differs between .NET runtimes.</summary>
public class FrameworkBehaviourTests
{
    [Fact]
    public void A_broken_link_still_counts_as_a_link_and_only_the_link_goes()
    {
        using var f = new Fixture();
        var target = f.Outside("will-vanish");
        var link = Path.Combine(f.Base, "broken-link");
        Fixture.CreateDirectoryLink(link, target);
        Directory.Delete(target, recursive: true);

        Assert.True(FileSystemWalk.IsReparsePoint(link));
        Assert.True(SafeDelete.TryRemoveLinkOnly(link));
        Assert.False(FileSystemWalk.IsReparsePoint(link));
    }

    [Fact]
    public void A_missing_path_is_not_a_link()
    {
        using var f = new Fixture();
        Assert.False(FileSystemWalk.IsReparsePoint(Path.Combine(f.Base, "nothing-here")));
    }

    [Fact]
    public void Deleting_a_tree_leaves_the_target_of_a_link_inside_it()
    {
        using var f = new Fixture();
        var target = f.Outside("precious-target");
        var tree = Path.Combine(f.Base, "tree");
        Directory.CreateDirectory(Path.Combine(tree, "sub"));
        Fixture.CreateDirectoryLink(Path.Combine(tree, "sub", "link"), target);

        SafeDelete.DeleteTree(tree);

        Assert.False(Directory.Exists(tree));
        Assert.True(File.Exists(Path.Combine(target, "precious.txt")));
    }

    [Fact]
    public void Measuring_does_not_count_or_enter_a_link()
    {
        using var f = new Fixture();
        var target = f.Outside("big-target");
        File.WriteAllBytes(Path.Combine(target, "big.bin"), new byte[5000]);
        var dir = Path.Combine(f.Base, "measured");
        Directory.CreateDirectory(dir);
        File.WriteAllBytes(Path.Combine(dir, "small.bin"), new byte[10]);
        Fixture.CreateDirectoryLink(Path.Combine(dir, "link"), target);

        Assert.Equal(10, FileSystemWalk.Measure(new DirectoryInfo(dir)).Bytes);
    }

    [Fact]
    public void Hidden_read_only_and_system_files_are_counted_and_removed()
    {
        using var f = new Fixture();
        var dir = Path.Combine(f.Base, "attributes");
        Directory.CreateDirectory(dir);
        foreach (var (name, attrs) in new[]
                 {
                     ("hidden.bin", FileAttributes.Hidden),
                     ("readonly.bin", FileAttributes.ReadOnly),
                     ("system.bin", FileAttributes.System),
                 })
        {
            var path = Path.Combine(dir, name);
            File.WriteAllBytes(path, new byte[100]);
            File.SetAttributes(path, attrs);
        }

        Assert.Equal(300, FileSystemWalk.Measure(new DirectoryInfo(dir)).Bytes);
        Assert.Equal(3, FileSystemWalk.Files(new DirectoryInfo(dir)).Count());

        SafeDelete.DeleteTree(dir);
        Assert.False(Directory.Exists(dir));
    }

    [Fact]
    public void The_real_drive_is_found_for_a_path_that_does_not_exist_yet()
    {
        using var f = new Fixture();
        var missing = Path.Combine(f.Base, "no", "such", "file.bin");
        Assert.Equal(Path.GetPathRoot(f.Base), WindowsRecycleBin.PhysicalDriveRoot(missing), ignoreCase: true);
    }

    [Fact]
    public void The_real_drive_is_found_through_a_link()
    {
        using var f = new Fixture();
        var target = f.Outside("real-place");
        var link = Path.Combine(f.Base, "linked");
        Fixture.CreateDirectoryLink(link, target);
        Assert.Equal(Path.GetPathRoot(target), WindowsRecycleBin.PhysicalDriveRoot(Path.Combine(link, "precious.txt")), ignoreCase: true);
    }

    [Fact]
    public void Short_paths_are_left_alone_and_long_ones_round_trip()
    {
        Assert.Equal(@"C:\short", PathEx.Extended(@"C:\short"));
        Assert.Equal(@"C:\short", PathEx.Plain(@"C:\short"));

        var deep = string.Join(@"\", Enumerable.Repeat(new string('a', 40), 8));
        var driveLong = @"C:\" + deep;
        Assert.Equal(@"\\?\" + driveLong, PathEx.Extended(driveLong));
        Assert.Equal(driveLong, PathEx.Plain(PathEx.Extended(driveLong)));
        Assert.Equal(PathEx.Extended(driveLong), PathEx.Extended(PathEx.Extended(driveLong)));

        var uncLong = @"\\server\share\" + deep;
        Assert.Equal(@"\\?\UNC\server\share\" + deep, PathEx.Extended(uncLong));
        Assert.Equal(uncLong, PathEx.Plain(PathEx.Extended(uncLong)));
    }

    [Fact]
    public void A_backup_includes_files_past_the_length_limit()
    {
        using var f = new Fixture();
        var work = Path.Combine(f.WasmRoot, "MSFS2024", "a", "work");
        var deep = work;
        while (deep.Length < 400) deep = Path.Combine(deep, new string('d', 40));
        Directory.CreateDirectory(PathEx.Extended(deep));
        File.WriteAllBytes(PathEx.Extended(Path.Combine(deep, "state.bin")), new byte[10]);
        var backups = f.Outside("backups");
        var target = new ConsignTarget("a", "a", "MSFS2024", work, 10);

        var zip = BackupZip.Create(target, backups, DateTime.Now);

        using var archive = System.IO.Compression.ZipFile.OpenRead(zip);
        Assert.Contains(archive.Entries, e => e.Name == "state.bin");
    }

    [Fact]
    public void The_guard_still_refuses_the_extended_form_of_a_path_inside_the_root()
    {
        using var f = new Fixture();
        var pkg = f.AddWasm("MSFS2024", "a");
        var guard = new PathGuard(new[] { f.WasmRoot });
        Assert.False(guard.Check(@"\\?\" + pkg).Allowed);
    }

    [Fact]
    public void Scanned_paths_stay_in_ordinary_form_when_the_root_is_very_long()
    {
        using var f = new Fixture();
        var root = f.WasmRoot;
        while (root.Length < 300) root = Path.Combine(root, new string('r', 40));
        var pkg = Path.Combine(root, "MSFS2024", "a");
        Directory.CreateDirectory(PathEx.Extended(Path.Combine(pkg, "work")));
        File.WriteAllBytes(PathEx.Extended(Path.Combine(pkg, "m0123456789abcdef_0.dll")), new byte[10]);

        var entry = Assert.Single(new WasmScanner(f.Config).Scan(root));

        Assert.Equal(pkg, entry.PackagePath);
        Assert.Equal(Path.Combine(pkg, "work"), entry.WorkPath);
        Assert.Equal(Path.Combine(pkg, "m0123456789abcdef_0.dll"), Assert.Single(entry.CompiledFiles));
        Assert.Equal(10, entry.CompiledSizeBytes);

        SafeDelete.DeleteTree(f.WasmRoot);
        Assert.False(Directory.Exists(f.WasmRoot));
    }

    [Theory]
    [InlineData(@"C:\a", true)]
    [InlineData(@"C:/a", true)]
    [InlineData(@"\\server\share", true)]
    [InlineData(@"\\?\C:\a", true)]
    [InlineData(@"C:", false)]
    [InlineData(@"C:a", false)]
    [InlineData(@"\a", false)]
    [InlineData(@"a\b", false)]
    [InlineData(@"", false)]
    public void Fully_qualified_paths_are_recognised(string path, bool expected) =>
        Assert.Equal(expected, PathEx.IsPathFullyQualified(path));

    [Theory]
    [InlineData(@"C:\a\", @"C:\a")]
    [InlineData(@"C:\a", @"C:\a")]
    [InlineData(@"C:\", @"C:\")]
    [InlineData(@"C:\a/", @"C:\a")]
    public void One_trailing_separator_is_trimmed_but_never_from_a_root(string path, string expected) =>
        Assert.Equal(expected, PathEx.TrimEndingDirectorySeparator(path));
}
