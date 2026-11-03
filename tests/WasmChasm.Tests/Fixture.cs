using WasmChasm.Core;

namespace WasmChasm.Tests;

/// <summary>Builds a synthetic install tree in a temp directory and removes it afterwards.</summary>
public sealed class Fixture : IDisposable
{
    public string Base { get; }
    public string WasmRoot { get; }
    public string Packages { get; }
    public string UserCfg { get; }
    public LocationsConfig Config { get; } = LocationsConfig.LoadEmbedded();

    public Fixture()
    {
        Base = Path.Combine(Path.GetTempPath(), "wasmchasm-tests", Guid.NewGuid().ToString("N"));
        WasmRoot = Path.Combine(Base, "AppData", "WASM");
        Packages = Path.Combine(Base, "Packages");
        UserCfg = Path.Combine(Base, "AppData", "UserCfg.opt");
        Directory.CreateDirectory(WasmRoot);
        Directory.CreateDirectory(Packages);
        File.WriteAllText(UserCfg, $"Version 1\nInstalledPackagesPath \"{Packages}\"\n");
    }

    public InstallLocation Install => new("steam", "STEAM", WasmRoot, UserCfg);

    /// <summary>Creates WASM\simVersion\package\work with a file of the given size.</summary>
    public string AddWasm(string simVersion, string package, int workBytes = 100, int otherBytes = 0)
    {
        var pkg = Path.Combine(WasmRoot, simVersion, package);
        var work = Path.Combine(pkg, "work");
        Directory.CreateDirectory(Path.Combine(work, "sub"));
        File.WriteAllBytes(Path.Combine(work, "sub", "state.bin"), new byte[workBytes]);
        if (otherBytes > 0) File.WriteAllBytes(Path.Combine(pkg, "module.wasm.cache"), new byte[otherBytes]);
        return pkg;
    }

    /// <summary>The file set the sim was observed generating for one compiled module.</summary>
    public static readonly string[] CompiledNames =
    {
        "m78edaad72d8f5958_0.cache", "m78edaad72d8f5958_0.dll", "m78edaad72d8f5958_0.lib",
        "m78edaad72d8f5958_0.natvis", "m78edaad72d8f5958_0.obj", "m78edaad72d8f5958_0.pdb",
    };

    /// <summary>Adds one module's compiled files beside the work folder, 10 bytes each.</summary>
    public void AddCompiled(string packagePath)
    {
        foreach (var name in CompiledNames) File.WriteAllBytes(Path.Combine(packagePath, name), new byte[10]);
    }

    /// <summary>A directory symlink where allowed, else a junction, which Windows lets any user create.</summary>
    public static void CreateDirectoryLink(string link, string target)
    {
        foreach (var kind in new[] { "/D", "/J" })
        {
            var psi = new System.Diagnostics.ProcessStartInfo("cmd.exe", $"/c mklink {kind} \"{link}\" \"{target}\"")
            {
                CreateNoWindow = true,
                UseShellExecute = false,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
            };
            using var p = System.Diagnostics.Process.Start(psi)!;
            p.WaitForExit();
            if (Directory.Exists(link)) return;
        }

        Assert.Fail("Could not create a directory link for the test.");
    }

    public void AddManifest(string relativeDir, string package, string title)
    {
        var dir = Path.Combine(Packages, relativeDir, package);
        Directory.CreateDirectory(dir);
        File.WriteAllText(Path.Combine(dir, "manifest.json"), $"{{ \"title\": \"{title}\", \"package_version\": \"1.0.0\" }}");
    }

    public string Outside(string name)
    {
        var dir = Path.Combine(Base, "outside", name);
        Directory.CreateDirectory(dir);
        File.WriteAllText(Path.Combine(dir, "precious.txt"), "do not delete");
        return dir;
    }

    public void Dispose()
    {
        try { SafeDelete.DeleteTree(Base); } catch { /* best effort */ }
    }
}

public sealed class FakeSim : ISimProcessDetector
{
    public int RunningAfterCalls { get; set; } = int.MaxValue;
    public int Calls { get; private set; }
    public bool IsSimRunning() => ++Calls > RunningAfterCalls;
}

public sealed class FakeRecycleBin : IRecycleBin
{
    public string? Unavailable { get; set; }
    public List<string> Recycled { get; } = new();
    public string? WhyCannotRecycle(string path, long sizeBytes) => Unavailable;

    public void Recycle(string path)
    {
        if (Unavailable is not null) throw new RecycleBinUnavailableException(Unavailable);
        Recycled.Add(path);
        if (File.Exists(path)) File.Delete(path);
        else SafeDelete.DeleteTree(path);
    }
}
