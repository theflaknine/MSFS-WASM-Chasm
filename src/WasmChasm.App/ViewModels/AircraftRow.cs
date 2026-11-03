using WasmChasm.Core;

namespace WasmChasm.App.ViewModels;

/// <summary>One ledger row: an aircraft and its sub-rows (one per sim-version folder).</summary>
public sealed class AircraftRow : ObservableObject
{
    private bool _isMarked;
    private bool _isDetached;
    private DeletionScope _scope;

    public AircraftRow(AircraftGroup group, DeletionScope scope)
    {
        Group = group;
        _scope = scope;
        SubRows = group.Entries.Select(e => new SubRow(e, this)).ToList();
    }

    public AircraftGroup Group { get; }
    public IReadOnlyList<SubRow> SubRows { get; }

    public string Title => Group.IsOrphan ? Group.PackageId : Group.DisplayName;
    public string PackageId => Group.PackageId;
    public bool IsOrphan => Group.IsOrphan;
    public string OrphanTag => Group.IsOrphan ? "ORPHANED" : "";
    public DateTime LastModified => Group.LastModifiedUtc;
    public string DateText => Group.LastModifiedUtc == DateTime.MinValue ? "----------" : Group.LastModifiedUtc.ToLocalTime().ToString("yyyy-MM-dd");

    /// <summary>Bytes this row would release under the current scope (known folders only).</summary>
    public long ScopedBytes => Group.ScopedSize(_scope);
    public string SizeText => ByteFormat.Format(ScopedBytes);

    /// <summary>Show sub-rows when there's more than one folder or any is unusual.</summary>
    public bool ShowSubRows => SubRows.Count > 1 || SubRows.Any(s => !s.Entry.IsKnownSimVersion || s.Entry.WorkIsReparsePoint || !s.Entry.HasWorkFolder);

    public DeletionScope Scope
    {
        get => _scope;
        set
        {
            if (!Set(ref _scope, value)) return;
            Raise(nameof(ScopedBytes));
            Raise(nameof(SizeText));
            foreach (var s in SubRows) s.Refresh();
        }
    }

    public bool IsMarked
    {
        get => _isMarked;
        set
        {
            if (Set(ref _isMarked, value)) Raise(nameof(Marker));
        }
    }

    public string Marker => _isMarked ? "■" : "·";

    /// <summary>True while the row's copy is animating into the chasm.</summary>
    public bool IsDetached
    {
        get => _isDetached;
        set => Set(ref _isDetached, value);
    }

    public string AccessibleName =>
        $"{Title}, package {PackageId}, {SizeText}, modified {DateText}{(IsOrphan ? ", orphaned" : "")}{(IsMarked ? ", marked" : "")}";
}

public sealed class SubRow : ObservableObject
{
    private readonly AircraftRow _owner;

    public SubRow(WasmEntry entry, AircraftRow owner)
    {
        Entry = entry;
        _owner = owner;
    }

    public WasmEntry Entry { get; }

    public string Text
    {
        get
        {
            long bytes = _owner.Scope == DeletionScope.WorkFolder ? Entry.WorkSizeBytes : Entry.SizeBytes;
            var flags = new List<string>();
            if (!Entry.IsKnownSimVersion) flags.Add("UNRECOGNISED FOLDER, NOT AUTO-CONSIGNED");
            if (Entry.WorkIsReparsePoint) flags.Add("LINK");
            if (!Entry.HasWorkFolder) flags.Add("NO WORK FOLDER");
            return $"  └ {Entry.SimVersionFolder}  {ByteFormat.Format(bytes)}" + (flags.Count > 0 ? "  [" + string.Join("; ", flags) + "]" : "");
        }
    }

    public void Refresh() => Raise(nameof(Text));
}
