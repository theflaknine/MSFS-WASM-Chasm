using WasmChasm.Core;

namespace WasmChasm.Tests;

public class BackupFolderRuleTests
{
    private static readonly string[] Roots = { @"C:\Users\a\Packages\Sim\LocalState\WASM" };

    [Theory]
    [InlineData(@"D:\Backups", @"D:\Backups")]
    [InlineData(@"D:\Backups\", @"D:\Backups")]
    [InlineData(@"  D:\Backups  ", @"D:\Backups")]
    [InlineData("\"D:\\My Backups\"", @"D:\My Backups")]
    [InlineData(@"D:\Backups\\\", @"D:\Backups")]
    [InlineData(@"D:/Backups/", @"D:\Backups")]
    [InlineData(@"D:\Backups/\/", @"D:\Backups")]
    [InlineData(@"D:\Backups\ ", @"D:\Backups")]
    [InlineData("\"D:\\My Backups\\\"", @"D:\My Backups")]
    [InlineData(@"D:\Old\\Backups\", @"D:\Old\Backups")]
    [InlineData(@"D:\", @"D:\")]
    [InlineData(@"D:\\\", @"D:\")]
    [InlineData(@"D:/", @"D:\")]
    [InlineData(@"\\nas\share\backups\", @"\\nas\share\backups")]
    [InlineData(@"\\nas\share\backups", @"\\nas\share\backups")]
    [InlineData(@"C:\Users\a\Packages\Sim\LocalState\WASM-backups", @"C:\Users\a\Packages\Sim\LocalState\WASM-backups")]
    public void Usable_paths_are_cleaned_up(string input, string expected)
    {
        Assert.Null(BackupFolderRule.Check(input, Roots, out var folder));
        Assert.Equal(expected, folder);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("\"\"")]
    [InlineData("backups")]
    [InlineData(@"..\backups")]
    [InlineData(@"D:backups")]
    [InlineData(@"\backups")]
    [InlineData(@"\\?\D:\backups")]
    [InlineData(@"D:\back|ups")]
    public void Unusable_paths_are_refused(string? input)
    {
        Assert.NotNull(BackupFolderRule.Check(input, Roots, out var folder));
        Assert.Null(folder);
    }

    [Theory]
    [InlineData(@"C:\Users\a\Packages\Sim\LocalState\WASM")]
    [InlineData(@"C:\Users\a\Packages\Sim\LocalState\WASM\")]
    [InlineData(@"C:\Users\a\Packages\Sim\LocalState\WASM\\\")]
    [InlineData(@"C:/Users/a/Packages/Sim/LocalState/WASM/")]
    [InlineData(@"c:\users\A\packages\sim\localstate\wasm\backups")]
    [InlineData(@"C:\Users\a\Packages\Sim\LocalState\WASM\MSFS2024\pkg")]
    [InlineData(@"C:\Users\a\Packages\Sim\LocalState\Other\..\WASM\backups")]
    public void A_folder_inside_the_wasm_folder_is_refused(string input)
    {
        Assert.Contains("INSIDE THE WASM FOLDER", BackupFolderRule.Check(input, Roots, out var folder));
        Assert.Null(folder);
    }

    [Fact]
    public void Every_detected_install_counts()
    {
        var roots = new[] { @"C:\Steam\WASM", @"E:\Store\WASM" };
        Assert.NotNull(BackupFolderRule.Check(@"E:\Store\WASM\backups", roots, out _));
    }
}
