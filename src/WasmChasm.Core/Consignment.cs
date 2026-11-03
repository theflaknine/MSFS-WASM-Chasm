namespace WasmChasm.Core;

public sealed class ConsignOptions
{
    public DeletionScope Scope { get; init; } = DeletionScope.WorkFolder;
    public bool DryRun { get; init; }
    /// <summary>Unknown sibling folders (e.g. MSFS2024_now_valid) are never included unless explicitly asked.</summary>
    public bool IncludeUnknownSimVersions { get; init; }
}

public enum PreflightProblem
{
    None,
    SimRunning,
    NothingToConsign,
    RecycleBinUnavailable,
}

public sealed record PreflightResult(PreflightProblem Problem, string Message, IReadOnlyList<ConsignTarget> Targets, IReadOnlyList<ConsignResult> Refused);

/// <summary>
/// Pre-flight (sim not running, path guard) → dispose per folder, emitting progress → log → results.
/// </summary>
public sealed class ConsignmentService
{
    private readonly IPathGuard _guard;
    private readonly ISimProcessDetector _sim;
    private readonly SessionLog? _log;
    private readonly IRecycleBin? _recycleBin;

    public ConsignmentService(IPathGuard guard, ISimProcessDetector sim, SessionLog? log, IRecycleBin? recycleBin = null)
    {
        _guard = guard;
        _sim = sim;
        _log = log;
        _recycleBin = recycleBin;
    }

    /// <summary>Turns selected aircraft into folder targets per scope (FR-10). Unknown siblings excluded by default.</summary>
    public static IReadOnlyList<ConsignTarget> BuildTargets(IEnumerable<AircraftGroup> groups, ConsignOptions options)
    {
        var targets = new List<ConsignTarget>();
        foreach (var group in groups)
        {
            foreach (var entry in group.Entries)
            {
                if (!entry.IsKnownSimVersion && !options.IncludeUnknownSimVersions) continue;
                if (options.Scope == DeletionScope.WorkFolder)
                {
                    if (!entry.HasWorkFolder) continue;
                    targets.Add(new ConsignTarget(group.PackageId, group.DisplayName, entry.SimVersionFolder, entry.WorkPath, entry.WorkSizeBytes));
                }
                else
                {
                    targets.Add(new ConsignTarget(group.PackageId, group.DisplayName, entry.SimVersionFolder, entry.PackagePath, entry.SizeBytes));
                }
            }
        }

        return targets;
    }

    /// <summary>Checks before anything is touched. Targets refused by the path guard are reported, not removed.</summary>
    public PreflightResult Preflight(IReadOnlyList<ConsignTarget> targets, DisposalMode mode)
    {
        var ok = new List<ConsignTarget>();
        var refused = new List<ConsignResult>();
        foreach (var t in targets)
        {
            var decision = _guard.Check(t.Path);
            if (decision.Allowed) ok.Add(t with { Path = decision.CanonicalPath! });
            else refused.Add(new ConsignResult(t, ConsignOutcome.Skipped, "Refused by path guard: " + decision.Reason));
        }

        if (_sim.IsSimRunning())
            return new PreflightResult(PreflightProblem.SimRunning,
                "MICROSOFT FLIGHT SIMULATOR 2024 IS RUNNING. CLOSE IT BEFORE CONSIGNING.", ok, refused);

        if (ok.Count == 0)
            return new PreflightResult(PreflightProblem.NothingToConsign, "NO ELIGIBLE FOLDERS IN SELECTION.", ok, refused);

        if (mode is DisposalMode.RecycleBin or DisposalMode.ZipThenRecycle && _recycleBin is not null)
        {
            foreach (var t in ok)
            {
                if (FileSystemWalk.IsReparsePoint(t.Path)) continue; // links are removed directly
                var why = _recycleBin.WhyCannotRecycle(t.Path, t.SizeBytes);
                if (why is not null)
                    return new PreflightResult(PreflightProblem.RecycleBinUnavailable, why, ok, refused);
            }
        }

        return new PreflightResult(PreflightProblem.None, "OK", ok, refused);
    }

    /// <summary>
    /// Disposes each target in turn. Re-checks the sim before every folder; if it starts, the rest are skipped.
    /// Failures are reported and the run continues (no elevation, no retries).
    /// </summary>
    public IReadOnlyList<ConsignResult> Execute(
        IReadOnlyList<ConsignTarget> targets,
        IDisposalStrategy strategy,
        ConsignOptions options,
        IProgress<ConsignProgress>? progress = null,
        CancellationToken cancellationToken = default)
    {
        var results = new List<ConsignResult>();
        _log?.BeginSession($"{(options.DryRun ? "DRY RUN" : "CONSIGN")} mode={strategy.Mode} scope={options.Scope} targets={targets.Count}");

        bool stopped = false;
        string stopReason = "";
        for (int i = 0; i < targets.Count; i++)
        {
            var target = targets[i];
            ConsignResult result;

            if (stopped)
            {
                result = new ConsignResult(target, ConsignOutcome.Skipped, stopReason);
            }
            else if (cancellationToken.IsCancellationRequested)
            {
                stopped = true;
                stopReason = "Cancelled by operator.";
                result = new ConsignResult(target, ConsignOutcome.Skipped, stopReason);
            }
            else if (_sim.IsSimRunning())
            {
                stopped = true;
                stopReason = "Simulator started during consignment.";
                result = new ConsignResult(target, ConsignOutcome.Skipped, stopReason);
            }
            else
            {
                result = DisposeOne(target, strategy, options);
            }

            results.Add(result);
            _log?.Write($"{result.Outcome,-11} {target.Path}  [{ByteFormat.Format(target.SizeBytes)}]  {result.Reason}" +
                        (result.BackupPath is null ? "" : $"  backup={result.BackupPath}"));
            progress?.Report(new ConsignProgress(i, targets.Count, result));
        }

        long reclaimed = results.Where(r => r.Outcome == ConsignOutcome.Removed).Sum(r => r.Target.SizeBytes);
        _log?.Write($"SUMMARY removed={results.Count(r => r.Outcome == ConsignOutcome.Removed)} " +
                    $"skipped={results.Count(r => r.Outcome == ConsignOutcome.Skipped)} " +
                    $"failed={results.Count(r => r.Outcome == ConsignOutcome.Failed)} reclaimed={ByteFormat.Format(reclaimed)}");
        return results;
    }

    private ConsignResult DisposeOne(ConsignTarget target, IDisposalStrategy strategy, ConsignOptions options)
    {
        // Path guard runs again immediately before every write.
        var decision = _guard.Check(target.Path);
        if (!decision.Allowed)
            return new ConsignResult(target, ConsignOutcome.Skipped, "Refused by path guard: " + decision.Reason);

        var path = decision.CanonicalPath!;
        bool isLink = FileSystemWalk.IsReparsePoint(path);
        if (!Directory.Exists(path) && !isLink)
            return new ConsignResult(target, ConsignOutcome.Skipped, "Folder no longer exists.");

        if (options.DryRun)
            return new ConsignResult(target, ConsignOutcome.WouldRemove,
                isLink ? "Dry run: would remove link only." : "Dry run: nothing touched.", LinkOnly: isLink);

        try
        {
            var report = strategy.Dispose(target with { Path = path });
            var reason = report.LinkOnly ? "Link removed; its target was not touched." : strategy.Mode switch
            {
                DisposalMode.RecycleBin => "Moved to Recycle Bin.",
                DisposalMode.ZipThenRecycle => "Backed up, then moved to Recycle Bin.",
                DisposalMode.ZipThenPermanent => "Backed up, then deleted.",
                _ => "Deleted permanently.",
            };
            return new ConsignResult(target, ConsignOutcome.Removed, reason, report.LinkOnly, report.BackupPath);
        }
        catch (RecycleBinUnavailableException ex)
        {
            return new ConsignResult(target, ConsignOutcome.Skipped, "Recycle Bin unavailable: " + ex.Message);
        }
        catch (UnauthorizedAccessException ex)
        {
            return new ConsignResult(target, ConsignOutcome.Failed, "Access denied: " + ex.Message);
        }
        catch (IOException ex)
        {
            return new ConsignResult(target, ConsignOutcome.Failed, "File locked or in use: " + ex.Message);
        }
        catch (Exception ex)
        {
            return new ConsignResult(target, ConsignOutcome.Failed, ex.GetType().Name + ": " + ex.Message);
        }
    }
}
