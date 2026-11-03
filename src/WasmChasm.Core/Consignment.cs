namespace WasmChasm.Core;

public sealed class ConsignOptions
{
    public DeletionScope Scope { get; init; } = DeletionScope.CompiledCache;
    public bool DryRun { get; init; }
    /// <summary>Unknown sibling folders (e.g. MSFS2024_now_valid) are never included unless explicitly asked.</summary>
    public bool IncludeUnknownSimVersions { get; init; }
}

/// <summary>The data-loss wording shown before acting, shared by the app and the CLI (FR-8).</summary>
public static class ScopeText
{
    public static string Warning(DeletionScope scope) => scope switch
    {
        DeletionScope.CompiledCache => "Settings are kept. The next load may take longer.",
        DeletionScope.WorkFolder =>
            "This erases settings and add-on data, such as navdata, account IDs and licence files. You may need to set them up again.",
        _ =>
            "This erases settings and add-on data, such as navdata, account IDs and licence files, plus the compiled files. You may need to set them up again.",
    };

    public static string Name(DeletionScope scope) => scope switch
    {
        DeletionScope.CompiledCache => "COMPILED CACHE ONLY",
        DeletionScope.WorkFolder => "WORK FOLDER ONLY",
        _ => "WHOLE PACKAGE FOLDER",
    };

    public static string ColumnHeading(DeletionScope scope) => scope switch
    {
        DeletionScope.CompiledCache => "CACHE",
        DeletionScope.WorkFolder => "WORK",
        _ => "SIZE",
    };

    /// <summary>
    /// True when the outcome can't be read from the animation alone: anything skipped or failed,
    /// a link removed instead of its target, or a backup written somewhere.
    /// </summary>
    public static bool NeedsResultsPage(IEnumerable<ConsignResult> results) =>
        results.Any(r => r.Outcome != ConsignOutcome.Removed || r.LinkOnly || r.BackupPath is not null);

    /// <summary>The closing line. Counts aircraft, not folders: one aircraft can have several sim-version folders.</summary>
    public static string Summary(IEnumerable<ConsignResult> results, bool dryRun)
    {
        var purged = results.Where(r => r.Outcome is ConsignOutcome.Removed or ConsignOutcome.WouldRemove).ToList();
        int aircraft = purged.Select(r => r.Target.PackageId).Distinct(StringComparer.OrdinalIgnoreCase).Count();
        var size = ByteFormat.Megabytes(purged.Sum(r => r.Target.SizeBytes));
        return dryRun
            ? $"DRY RUN DONE. {aircraft} AIRCRAFT WOULD BE PURGED. {size} WOULD BE FREED."
            : $"DONE. {aircraft} AIRCRAFT PURGED. {size} FREED.";
    }

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
                if (!entry.HasContentFor(options.Scope)) continue;
                var size = entry.SizeFor(options.Scope);
                targets.Add(options.Scope switch
                {
                    DeletionScope.CompiledCache => new ConsignTarget(group.PackageId, group.DisplayName, entry.SimVersionFolder, entry.PackagePath, size, entry.CompiledFiles),
                    DeletionScope.WorkFolder => new ConsignTarget(group.PackageId, group.DisplayName, entry.SimVersionFolder, entry.WorkPath, size),
                    _ => new ConsignTarget(group.PackageId, group.DisplayName, entry.SimVersionFolder, entry.PackagePath, size),
                });
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
            var guarded = Guard(t, out var reason);
            if (guarded is not null) ok.Add(guarded);
            else refused.Add(new ConsignResult(t, ConsignOutcome.Skipped, "Refused by path guard: " + reason));
        }

        if (_sim.IsSimRunning())
            return new PreflightResult(PreflightProblem.SimRunning,
                "MICROSOFT FLIGHT SIMULATOR 2024 IS RUNNING. CLOSE IT BEFORE PURGING.", ok, refused);

        if (ok.Count == 0)
            return new PreflightResult(PreflightProblem.NothingToConsign, "NOTHING TO PURGE IN THIS SELECTION.", ok, refused);

        if (mode is DisposalMode.RecycleBin or DisposalMode.ZipThenRecycle && _recycleBin is not null)
        {
            foreach (var t in ok)
            {
                if (t.Files is null && FileSystemWalk.IsReparsePoint(t.Path)) continue; // links are removed directly
                foreach (var path in t.Files ?? new[] { t.Path })
                {
                    var why = _recycleBin.WhyCannotRecycle(path, t.SizeBytes);
                    if (why is not null)
                        return new PreflightResult(PreflightProblem.RecycleBinUnavailable, why, ok, refused);
                }
            }
        }

        return new PreflightResult(PreflightProblem.None, "OK", ok, refused);
    }

    /// <summary>Runs the target's folder and every listed file through the path guard. Null if any is refused.</summary>
    private ConsignTarget? Guard(ConsignTarget target, out string reason)
    {
        reason = "";
        var folder = _guard.Check(target.Path);
        if (!folder.Allowed)
        {
            reason = folder.Reason;
            return null;
        }

        if (target.Files is null) return target with { Path = folder.CanonicalPath! };

        var files = new List<string>(target.Files.Count);
        foreach (var file in target.Files)
        {
            var decision = _guard.Check(file);
            if (!decision.Allowed)
            {
                reason = decision.Reason;
                return null;
            }

            files.Add(decision.CanonicalPath!);
        }

        return target with { Path = folder.CanonicalPath!, Files = files };
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
        _log?.BeginSession($"{(options.DryRun ? "DRY RUN" : "PURGE")} mode={strategy.Mode} scope={options.Scope} targets={targets.Count}");

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
                stopReason = "Simulator started while purging.";
                result = new ConsignResult(target, ConsignOutcome.Skipped, stopReason);
            }
            else
            {
                result = DisposeOne(target, strategy, options);
            }

            results.Add(result);
            _log?.Write($"{result.Outcome,-11} {target.Label}  [{ByteFormat.Format(target.SizeBytes)}]  {result.Reason}" +
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
        var guarded = Guard(target, out var refusal);
        if (guarded is null)
            return new ConsignResult(target, ConsignOutcome.Skipped, "Refused by path guard: " + refusal);

        var path = guarded.Path;
        bool isLink = guarded.Files is null && FileSystemWalk.IsReparsePoint(path);
        if (guarded.Files is not null)
        {
            // Anything that vanished or turned into a link since the scan is left alone.
            var present = guarded.Files.Where(f => File.Exists(PathEx.Extended(f)) && !FileSystemWalk.IsReparsePoint(f)).ToList();
            if (present.Count == 0)
                return new ConsignResult(target, ConsignOutcome.Skipped, "Compiled files no longer exist.");
            guarded = guarded with { Files = present };
        }
        else if (!Directory.Exists(PathEx.Extended(path)) && !isLink)
        {
            return new ConsignResult(target, ConsignOutcome.Skipped, "Folder no longer exists.");
        }

        if (options.DryRun)
            return new ConsignResult(target, ConsignOutcome.WouldRemove,
                isLink ? "Dry run: would remove link only." : "Dry run: nothing touched.", LinkOnly: isLink);

        try
        {
            var report = strategy.Dispose(guarded);
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
