using System.Text.Json;
using WasmChasm.Core;

namespace WasmChasm.Cli;

/// <summary>Thin console wrapper over Core: <c>scan</c> and <c>consign</c> (dry run by default for support use).</summary>
public static class Program
{
    private const string Usage = """
        WASM CHASM - command line

        USAGE
          wasmchasm scan    [--store steam|store] [--root <WASM folder>] [--json]
          wasmchasm consign <package-id>... [--store steam|store] [--root <WASM folder>]
                            [--dry-run] [--scope work|package] [--include-unknown]
                            [--mode recycle|zip|permanent] [--backup <folder>] [--yes]
          wasmchasm stores
          wasmchasm --help

        consign without --yes is always a dry run. Nothing outside the WASM root is ever touched.
        Not affiliated with Microsoft or Asobo Studio.
        """;

    public static int Main(string[] args)
    {
        try
        {
            return Run(args, Console.Out, Console.Error);
        }
        catch (CliException ex)
        {
            Console.Error.WriteLine("ERROR: " + ex.Message);
            return 2;
        }
    }

    public static int Run(string[] args, TextWriter stdout, TextWriter stderr, LocationsConfig? config = null)
    {
        if (args.Length == 0 || args[0] is "-h" or "--help" or "help")
        {
            stdout.WriteLine(Usage);
            return args.Length == 0 ? 1 : 0;
        }

        config ??= LocationsConfig.Load();
        var opts = CliOptions.Parse(args.Skip(1).ToArray());
        return args[0] switch
        {
            "stores" => Stores(config, stdout),
            "scan" => Scan(config, opts, stdout),
            "consign" => Consign(config, opts, stdout),
            _ => throw new CliException($"Unknown command '{args[0]}'. Try --help."),
        };
    }

    private static int Stores(LocationsConfig config, TextWriter o)
    {
        foreach (var locator in InstallDetection.CreateLocators(config))
        {
            var found = locator.Locate();
            o.WriteLine(found is null
                ? $"{locator.StoreId,-6} not found"
                : $"{locator.StoreId,-6} {found.WasmRoot}  (UserCfg.opt: {found.UserCfgPath ?? "not found"})");
        }

        return 0;
    }

    private static InstallLocation ResolveInstall(LocationsConfig config, CliOptions opts)
    {
        if (opts.Root is not null)
        {
            var full = Path.GetFullPath(opts.Root);
            if (!Directory.Exists(full)) throw new CliException($"WASM folder not found: {full}");
            return InstallLocation.Custom(full);
        }

        var found = InstallDetection.DetectAll(InstallDetection.CreateLocators(config));
        if (opts.Store is not null)
        {
            return found.FirstOrDefault(f => string.Equals(f.StoreId, opts.Store, StringComparison.OrdinalIgnoreCase))
                   ?? throw new CliException($"No {opts.Store} install found. Use --root to point at a WASM folder.");
        }

        return found.Count switch
        {
            0 => throw new CliException("No MSFS 2024 WASM folder found for Steam or Microsoft Store. Use --root."),
            1 => found[0],
            _ => throw new CliException("Both Steam and Store installs found. Choose one with --store steam|store."),
        };
    }

    private static int Scan(LocationsConfig config, CliOptions opts, TextWriter o)
    {
        var install = ResolveInstall(config, opts);
        var groups = Catalog.Load(install, config);
        var packagesPath = UserCfgParser.ReadInstalledPackagesPath(install.UserCfgPath);
        bool titlesKnown = packagesPath is not null && Directory.Exists(packagesPath);

        if (opts.Json)
        {
            var payload = new
            {
                store = install.StoreId,
                wasmRoot = install.WasmRoot,
                userCfg = install.UserCfgPath,
                aircraft = groups.Select(g => new
                {
                    packageId = g.PackageId,
                    title = g.Title,
                    orphan = titlesKnown && g.IsOrphan,
                    sizeBytes = g.SizeBytes,
                    workSizeBytes = g.WorkSizeBytes,
                    lastModifiedUtc = g.LastModifiedUtc,
                    folders = g.Entries.Select(e => new
                    {
                        simVersion = e.SimVersionFolder,
                        known = e.IsKnownSimVersion,
                        path = e.PackagePath,
                        hasWork = e.HasWorkFolder,
                        workIsLink = e.WorkIsReparsePoint,
                        sizeBytes = e.SizeBytes,
                        workSizeBytes = e.WorkSizeBytes,
                        lastModifiedUtc = e.LastModifiedUtc,
                    }),
                }),
            };
            o.WriteLine(JsonSerializer.Serialize(payload, new JsonSerializerOptions { WriteIndented = true }));
            return 0;
        }

        o.WriteLine($"INSTALL  {install.Label}  {install.WasmRoot}");
        o.WriteLine($"USERCFG  {install.UserCfgPath ?? "(not found - titles unavailable)"}");
        o.WriteLine();
        o.WriteLine($"{"TITLE",-40} {"PACKAGE",-36} {"SIZE",10} {"WORK",10}  MODIFIED");
        foreach (var g in groups)
        {
            var title = g.IsOrphan ? (titlesKnown ? "[ORPHAN] " : "") + g.PackageId : g.DisplayName;
            o.WriteLine($"{Trunc(title, 40),-40} {Trunc(g.PackageId, 36),-36} {ByteFormat.Format(g.SizeBytes),10} {ByteFormat.Format(g.WorkSizeBytes),10}  {g.LastModifiedUtc.ToLocalTime():yyyy-MM-dd}");
            foreach (var e in g.Entries)
            {
                var flags = (e.IsKnownSimVersion ? "" : " [UNRECOGNISED FOLDER]") + (e.WorkIsReparsePoint ? " [LINK]" : "") + (e.HasWorkFolder ? "" : " [NO WORK FOLDER]");
                o.WriteLine($"  └ {e.SimVersionFolder,-36}{"",40} {ByteFormat.Format(e.SizeBytes),10} {ByteFormat.Format(e.WorkSizeBytes),10}{flags}");
            }
        }

        o.WriteLine();
        o.WriteLine($"{groups.Count} aircraft, {ByteFormat.Format(groups.Sum(g => g.SizeBytes))} total.");
        return 0;
    }

    private static int Consign(LocationsConfig config, CliOptions opts, TextWriter o)
    {
        if (opts.Packages.Count == 0) throw new CliException("Name at least one package ID.");
        var install = ResolveInstall(config, opts);
        var groups = Catalog.Load(install, config);

        var selected = new List<AircraftGroup>();
        foreach (var id in opts.Packages)
        {
            var g = groups.FirstOrDefault(x => string.Equals(x.PackageId, id, StringComparison.OrdinalIgnoreCase))
                    ?? throw new CliException($"Package '{id}' not found under {install.WasmRoot}.");
            selected.Add(g);
        }

        bool dryRun = opts.DryRun || !opts.Yes;
        var options = new ConsignOptions { DryRun = dryRun, Scope = opts.Scope, IncludeUnknownSimVersions = opts.IncludeUnknown };
        var recycle = RecycleBins.ForCurrentPlatform();
        var service = new ConsignmentService(Catalog.GuardFor(install), new SimProcessDetector(config.SimProcessNames), SessionLog.Default(), recycle);

        var targets = ConsignmentService.BuildTargets(selected, options);
        var pre = service.Preflight(targets, opts.Mode);
        foreach (var r in pre.Refused) o.WriteLine($"SKIPPED   {r.Target.Path}  {r.Reason}");

        if (pre.Problem == PreflightProblem.SimRunning || pre.Problem == PreflightProblem.NothingToConsign)
        {
            o.WriteLine(pre.Message);
            return 3;
        }

        if (pre.Problem == PreflightProblem.RecycleBinUnavailable && !dryRun)
        {
            o.WriteLine($"RECYCLE BIN UNAVAILABLE: {pre.Message}");
            o.WriteLine("Re-run with --mode zip --backup <folder> to back up first, or cancel. Nothing was removed.");
            return 4;
        }

        o.WriteLine(dryRun ? "DRY RUN - NOTHING WILL BE TOUCHED." : "CONSIGNING.");
        o.WriteLine("WARNING: clearing a work folder erases this aircraft's settings, saved states and any navdata or");
        o.WriteLine("account IDs (for example SimBrief alias, Hoppie ID) stored there.");

        var mode = opts.Mode == DisposalMode.ZipThenRecycle && !OperatingSystem.IsWindows() ? DisposalMode.ZipThenPermanent : opts.Mode;
        var strategy = dryRun ? new PermanentStrategy() : DisposalStrategies.Create(mode, recycle, opts.Backup);
        var results = service.Execute(pre.Targets, strategy, options);

        foreach (var r in results)
            o.WriteLine($"{OutcomeLabel(r.Outcome),-12} {r.Target.Path}  [{ByteFormat.Format(r.Target.SizeBytes)}]  {r.Reason}{(r.BackupPath is null ? "" : "  backup: " + r.BackupPath)}");

        var removed = results.Where(r => r.Outcome is ConsignOutcome.Removed or ConsignOutcome.WouldRemove).ToList();
        o.WriteLine(dryRun
            ? $"DRY RUN COMPLETE. {removed.Count} RECORDS WOULD BE RELEASED. {ByteFormat.Megabytes(removed.Sum(r => r.Target.SizeBytes))} WOULD BE RECLAIMED."
            : $"CONSIGNMENT COMPLETE. {removed.Count} RECORDS RELEASED. {ByteFormat.Megabytes(removed.Sum(r => r.Target.SizeBytes))} RECLAIMED.");
        return results.Any(r => r.Outcome == ConsignOutcome.Failed) ? 5 : 0;
    }

    private static string OutcomeLabel(ConsignOutcome o) => o switch
    {
        ConsignOutcome.WouldRemove => "WOULD-REMOVE",
        _ => o.ToString().ToUpperInvariant(),
    };

    private static string Trunc(string s, int n) => s.Length <= n ? s : s[..(n - 1)] + "…";
}

public sealed class CliException : Exception
{
    public CliException(string message) : base(message) { }
}

public sealed class CliOptions
{
    public string? Store { get; private set; }
    public string? Root { get; private set; }
    public bool Json { get; private set; }
    public bool DryRun { get; private set; }
    public bool Yes { get; private set; }
    public bool IncludeUnknown { get; private set; }
    public DeletionScope Scope { get; private set; } = DeletionScope.WorkFolder;
    public DisposalMode Mode { get; private set; } = DisposalMode.RecycleBin;
    public string? Backup { get; private set; }
    public List<string> Packages { get; } = new();

    public static CliOptions Parse(string[] args)
    {
        var o = new CliOptions();
        for (int i = 0; i < args.Length; i++)
        {
            string Next() => i + 1 < args.Length ? args[++i] : throw new CliException($"{args[i]} needs a value.");
            switch (args[i])
            {
                case "--store": o.Store = Next(); break;
                case "--root": o.Root = Next(); break;
                case "--json": o.Json = true; break;
                case "--dry-run": o.DryRun = true; break;
                case "--yes": o.Yes = true; break;
                case "--include-unknown": o.IncludeUnknown = true; break;
                case "--backup": o.Backup = Path.GetFullPath(Next()); break;
                case "--scope":
                    o.Scope = Next() switch
                    {
                        "work" => DeletionScope.WorkFolder,
                        "package" => DeletionScope.PackageFolder,
                        var s => throw new CliException($"Unknown scope '{s}'."),
                    };
                    break;
                case "--mode":
                    o.Mode = Next() switch
                    {
                        "recycle" => DisposalMode.RecycleBin,
                        "zip" => DisposalMode.ZipThenRecycle,
                        "permanent" => DisposalMode.Permanent,
                        var s => throw new CliException($"Unknown mode '{s}'."),
                    };
                    break;
                default:
                    if (args[i].StartsWith("--", StringComparison.Ordinal)) throw new CliException($"Unknown option '{args[i]}'.");
                    o.Packages.Add(args[i]);
                    break;
            }
        }

        if (o.Mode == DisposalMode.ZipThenRecycle && o.Backup is null)
            throw new CliException("--mode zip needs --backup <folder>.");
        return o;
    }
}
