using System.Collections.ObjectModel;
using System.ComponentModel;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Media.Effects;
using System.Windows.Threading;
using Microsoft.Win32;
using WasmChasm.App.Crt;
using WasmChasm.App.Services;
using WasmChasm.App.ViewModels;
using WasmChasm.Core;

namespace WasmChasm.App;

public partial class MainWindow : Window
{
    private enum SortKey { Name, Size, Date }

    private readonly LocationsConfig _config;
    private readonly AppSettings _settings;
    private readonly SessionLog _log = SessionLog.Default();
    private readonly SoundBank _sound = new();
    private readonly IRecycleBin _recycleBin = RecycleBins.ForCurrentPlatform();
    private readonly ObservableCollection<AircraftRow> _rows = new();
    private readonly ICollectionView _view;
    private readonly DateTime _sessionStart = DateTime.Now;
    private readonly DispatcherTimer _clock;
    private readonly Random _rng = new();

    private IReadOnlyList<InstallLocation> _detected = Array.Empty<InstallLocation>();
    private InstallLocation? _install;
    private SortKey _sortKey = SortKey.Name;
    private bool _sortDescending;
    private bool _scanning;
    private bool _consigning;
    private CancellationTokenSource? _consignCts;
    private Func<Key, bool>? _sheetKeys;
    private Task _animationTail = Task.CompletedTask;
    private int _pendingAnimations;
    private bool _rendered;

    public MainWindow()
    {
        InitializeComponent();
        ContentRendered += (_, _) => _rendered = true;
        _config = LoadConfig();
        _settings = AppSettings.Load();
        ApplyMode();
        Activated += (_, _) => _sound.WindowActive = true;
        Deactivated += (_, _) => _sound.WindowActive = false;

        _view = CollectionViewSource.GetDefaultView(_rows);
        _view.Filter = FilterRow;
        Ledger.ItemsSource = _view;
        Ledger.AddHandler(Keyboard.GotKeyboardFocusEvent, new KeyboardFocusChangedEventHandler(OnLedgerFocusChanged));
        ApplySort();

        _clock = new DispatcherTimer { Interval = TimeSpan.FromSeconds(1) };
        _clock.Tick += (_, _) => UpdateClock();
        _clock.Start();
        UpdateClock();

        Loaded += OnLoaded;
        Closing += (_, _) =>
        {
            _settings.Save();
            _sound.StopHum();
            _sound.Dispose();
        };
        SystemParameters.StaticPropertyChanged += (_, e) =>
        {
            if (e.PropertyName == nameof(SystemParameters.HighContrast))
                Dispatcher.BeginInvoke(() => { ApplyCrt(); Chasm.Redraw(); });
        };
    }

    private static LocationsConfig LoadConfig()
    {
        try { return LocationsConfig.Load(); }
        catch (Exception) { return LocationsConfig.LoadEmbedded(); }
    }

    private void OnLoaded(object sender, RoutedEventArgs e)
    {
        ApplyCrt();
        _log.Write($"APP START v{typeof(MainWindow).Assembly.GetName().Version}");
        if (_settings.ModeChosen) StartUp();
        else ShowModeChoice();
    }

    /// <summary>The theatre, if this mode has one, then the scan. The theatre never holds up the scan.</summary>
    private void StartUp()
    {
        if (!IsPlain)
        {
            PreparePowerOn();
            PrepareSplash();
        }

        DetectAndChoose();
    }

    /// <summary>Runs once the window has been drawn: straight away if it already has been.</summary>
    private void WhenRendered(Action action)
    {
        if (_rendered)
        {
            Dispatcher.BeginInvoke(action, DispatcherPriority.Loaded);
            return;
        }

        void Once(object? sender, EventArgs e)
        {
            ContentRendered -= Once;
            action();
        }

        ContentRendered += Once;
    }

    /// <summary>The first-run question. It comes before everything else, so Serious users never see the theatre.</summary>
    private void ShowModeChoice()
    {
        ShowSheet("CHOOSE A MODE", k =>
        {
            AppMode? choice = k switch
            {
                Key.D1 or Key.NumPad1 => AppMode.Retro,
                Key.D2 or Key.NumPad2 => AppMode.Plain,
                _ => null,
            };
            if (choice is null) return false;

            _settings.ModeChosen = true;
            SetMode(choice.Value);
            CloseSheet();
            StartUp();
            return true;
        });

        Line("1  FUN");
        Line("   AMBER SCREEN, ANIMATIONS AND OPTIONAL SOUND.");
        Gap();
        Line("2  SERIOUS");
        Line("   PLAIN SCREEN. NO EFFECTS, ANIMATIONS OR SOUND.");
        Gap();
        Line("BOTH DO THE SAME THING TO YOUR FILES. F4 SWITCHES MODE AT ANY TIME (EXCEPT NOW).");
        Prompt("PRESS 1 OR 2.");
    }

    // ───────────────────────────── Install detection ─────────────────────────────

    private void DetectAndChoose()
    {
        _detected = InstallDetection.DetectAll(InstallDetection.CreateLocators(_config));

        var preferred = _detected.FirstOrDefault(d => d.StoreId == _settings.PreferredStoreId);
        if (preferred is not null)
        {
            UseInstall(preferred);
            return;
        }

        switch (_detected.Count)
        {
            case 0:
                ShowNoInstall();
                break;
            case 1:
                UseInstall(_detected[0]);
                break;
            default:
                ShowInstallPicker(firstRun: true);
                break;
        }
    }

    private void UseInstall(InstallLocation install)
    {
        _install = install;
        _settings.PreferredStoreId = install.StoreId;
        _settings.Save();
        InstallText.Text = $"INSTALL: {install.Label}  [F2]";
        _log.Write($"INSTALL {install.StoreId} root={install.WasmRoot} usercfg={install.UserCfgPath ?? "-"}");
        CloseSheet();
        _ = RescanAsync();
    }

    private async Task RescanAsync()
    {
        if (_install is null || _scanning || _consigning) return;
        var install = _install;
        _scanning = true;
        StatusText.Text = "SCANNING " + install.WasmRoot.ToUpperInvariant() + " ...";
        EmptyText.Visibility = Visibility.Collapsed;

        try
        {
            var (groups, titles) = await Task.Run(() =>
            {
                var g = Catalog.Load(install, _config);
                var packages = UserCfgParser.ReadInstalledPackagesPath(install.UserCfgPath);
                return (g, packages is not null && Directory.Exists(packages));
            });

            _rows.Clear();
            foreach (var g in groups) _rows.Add(new AircraftRow(g, _settings.Scope));

            ApplySort();
            UpdateTotals();
            SizeHeading.Text = ScopeText.ColumnHeading(_settings.Scope);

            var status = $"{groups.Count} AIRCRAFT FOUND. {ByteFormat.Format(groups.Sum(x => x.ScopedSize(_settings.Scope)))} CAN BE PURGED.";
            if (!titles) status += " NAMES UNAVAILABLE (USERCFG.OPT NOT FOUND).";
            if (groups.Count > 0 && !groups.SelectMany(x => x.Entries).Any(en => en.IsKnownSimVersion))
                status += " NO MSFS2024 OR MSFS2020 FOLDER FOUND.";
            if (_settings.DryRun) status += " DRY RUN IS ON.";
            StatusText.Text = status;

            EmptyText.Text = "NO AIRCRAFT FOUND.\nNO WASM DATA WAS FOUND UNDER\n" + install.WasmRoot;
            EmptyText.Visibility = groups.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
            _log.Write($"SCAN {groups.Count} aircraft");
        }
        catch (Exception ex)
        {
            StatusText.Text = "SCAN FAILED: " + ex.Message.ToUpperInvariant();
            _log.Write("SCAN FAILED " + ex);
        }
        finally
        {
            _scanning = false;
        }

        FocusLedger();
    }

    // ───────────────────────────── Ledger: filter, sort, totals ─────────────────────────────

    private bool FilterRow(object o)
    {
        var text = FilterBox.Text.Trim();
        if (text.Length == 0) return true;
        var row = (AircraftRow)o;
        return row.Title.Contains(text, StringComparison.OrdinalIgnoreCase) ||
               row.PackageId.Contains(text, StringComparison.OrdinalIgnoreCase);
    }

    private void OnFilterChanged(object sender, TextChangedEventArgs e)
    {
        _view.Refresh();
        UpdateTotals();
    }

    private void CycleSort(bool reverseOnly)
    {
        if (reverseOnly) _sortDescending = !_sortDescending;
        else
        {
            _sortKey = (SortKey)(((int)_sortKey + 1) % 3);
            _sortDescending = _sortKey != SortKey.Name; // biggest / newest first
        }

        ApplySort();
    }

    private void ApplySort()
    {
        var dir = _sortDescending ? ListSortDirection.Descending : ListSortDirection.Ascending;
        var prop = _sortKey switch
        {
            SortKey.Size => nameof(AircraftRow.ScopedBytes),
            SortKey.Date => nameof(AircraftRow.LastModified),
            _ => nameof(AircraftRow.Title),
        };
        using (_view.DeferRefresh())
        {
            _view.SortDescriptions.Clear();
            _view.SortDescriptions.Add(new SortDescription(prop, dir));
            if (_sortKey != SortKey.Name)
                _view.SortDescriptions.Add(new SortDescription(nameof(AircraftRow.Title), ListSortDirection.Ascending));
        }

        SortText.Text = $"SORT: {_sortKey.ToString().ToUpperInvariant()} {(_sortDescending ? "▼" : "▲")}  [F6]";
    }

    private void OnSortClick(object sender, RoutedEventArgs e) => CycleSort(reverseOnly: false);

    private void UpdateTotals()
    {
        var marked = _rows.Where(r => r.IsMarked && !r.IsDetached).ToList();
        long bytes = marked.Sum(r => r.ScopedBytes);
        int hidden = marked.Count(r => !_view.Filter(r));
        TotalText.Text = $"{marked.Count} MARKED. {ByteFormat.Megabytes(bytes)}." +
                         (hidden > 0 ? $" ({hidden} HIDDEN BY FILTER)" : "");
        Chasm.SetLevels(bytes, _rows.Sum(r => r.ScopedBytes));
    }

    private void OnLedgerSelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        UpdateTotals();
        if (e.AddedItems.Count + e.RemovedItems.Count > 0 && !_consigning && !_bulkMarking) _sound.PlayRatchet(MarkedCount);
    }

    /// <summary>Row to row only, so returning from a screen or the filter box stays silent.</summary>
    private void OnLedgerFocusChanged(object sender, KeyboardFocusChangedEventArgs e)
    {
        if (!_consigning && e.OldFocus is ListBoxItem && e.NewFocus is ListBoxItem && !ReferenceEquals(e.OldFocus, e.NewFocus))
            _sound.PlayTick();
    }

    private void OnLedgerPreviewKeyDown(object sender, KeyEventArgs e)
    {
        if (Keyboard.FocusedElement is not ListBoxItem item) return;

        switch (e.Key)
        {
            case Key.Space: item.IsSelected = !item.IsSelected; e.Handled = true; break;
            case Key.Right: item.IsSelected = true; e.Handled = true; break;
            case Key.Left: item.IsSelected = false; e.Handled = true; break;
        }
    }

    private void FocusLedger()
    {
        if (Sheet.Visibility == Visibility.Visible) return;
        Dispatcher.BeginInvoke(DispatcherPriority.Input, () =>
        {
            if (Ledger.Items.Count == 0)
            {
                Ledger.Focus();
                return;
            }

            var current = Keyboard.FocusedElement as ListBoxItem;
            if (current is not null && ReferenceEquals(ItemsControl.ItemsControlFromItemContainer(current), Ledger)) return;
            var first = Ledger.ItemContainerGenerator.ContainerFromIndex(0) as ListBoxItem;
            if (first is not null) first.Focus();
            else Ledger.Focus();
        });
    }

    private bool _bulkMarking;

    private int MarkedCount => _rows.Count(r => r.IsMarked && !r.IsDetached);

    private void SetMarks(Func<AircraftRow, bool> predicate, bool value)
    {
        // One tooth at the final level, not a burst of them.
        _bulkMarking = true;
        foreach (var r in _rows.Where(r => !r.IsDetached && predicate(r))) r.IsMarked = value;
        _bulkMarking = false;
        UpdateTotals();
        _sound.PlayRatchet(MarkedCount);
    }

    // ───────────────────────────── Keyboard ─────────────────────────────

    private void OnPreviewKeyDown(object sender, KeyEventArgs e)
    {
        if (_splashUp)
        {
            // A key still held from the mode choice shouldn't skip the splash it just started.
            if (!e.IsRepeat) DismissSplash();
            e.Handled = true;
            return;
        }

        var key = e.Key == Key.System ? e.SystemKey : e.Key;
        // Alt+F4 must always reach Windows, or the window can't be closed from a full-screen page.
        if (key == Key.F4 && (Keyboard.Modifiers & ModifierKeys.Alt) != 0) return;

        if (HandleKey(key, Keyboard.Modifiers)) e.Handled = true;
        else if (Sheet.Visibility == Visibility.Visible && Keyboard.FocusedElement is not TextBox) e.Handled = true;
    }

    private void OnLegendClick(object sender, RoutedEventArgs e)
    {
        if (sender is FrameworkElement { Tag: string tag } && Enum.TryParse<Key>(tag, out var key))
        {
            if (key == Key.Space)
            {
                if (Keyboard.FocusedElement is ListBoxItem item) item.IsSelected = !item.IsSelected;
                else FocusLedger();
                return;
            }

            HandleKey(key, ModifierKeys.None);
        }
    }

    private bool HandleKey(Key key, ModifierKeys mods)
    {
        if (_consigning)
        {
            if (key == Key.Escape && _consignCts is { IsCancellationRequested: false })
            {
                _consignCts.Cancel();
                StatusText.Text = "STOPPING AFTER THE CURRENT FOLDER.";
            }

            return true; // the ledger is locked while consigning
        }

        if (Sheet.Visibility == Visibility.Visible)
            return _sheetKeys?.Invoke(key) ?? false;

        // Only appearance changes, so this is safe during a scan. Alt+F4 must still close the window.
        if (key == Key.F4 && (mods & ModifierKeys.Alt) == 0)
        {
            ToggleMode();
            return true;
        }

        if (_scanning) return true;

        bool inFilter = FilterBox.IsKeyboardFocusWithin;
        bool ctrl = (mods & ModifierKeys.Control) != 0;
        bool shift = (mods & ModifierKeys.Shift) != 0;

        switch (key)
        {
            case Key.F1: ShowHelp(); return true;
            case Key.F2: ShowInstallPicker(firstRun: false); return true;
            case Key.F3: ShowSettings(); return true;
            case Key.F5: _ = RescanAsync(); return true;
            case Key.F6: CycleSort(reverseOnly: shift); return true;
            case Key.Enter:
                if (inFilter) FocusLedger();
                else BeginConsign();
                return true;
            case Key.Escape:
                if (inFilter)
                {
                    if (FilterBox.Text.Length > 0) FilterBox.Clear();
                    else FocusLedger();
                }
                else if (_rows.Any(r => r.IsMarked)) SetMarks(_ => true, false);
                return true;
            case Key.Down when inFilter:
                FocusLedger();
                return true;
            case Key.F when ctrl:
            case Key.OemQuestion when !inFilter:
            case Key.Divide when !inFilter:
                FilterBox.Focus();
                FilterBox.SelectAll();
                return true;
            case Key.A when ctrl && !inFilter:
                SetMarks(r => _view.Filter(r), true);
                return true;
            case Key.D when ctrl && !inFilter:
                SetMarks(_ => true, false);
                return true;
        }

        return false;
    }

    // ───────────────────────────── Sheet helpers ─────────────────────────────

    private void ShowSheet(string heading, Func<Key, bool> keys)
    {
        SheetHeading.Text = heading;
        AutomationPropertiesHelper.SetName(Sheet, heading);
        SheetBody.Children.Clear();
        SheetPrompt.Children.Clear();
        _sheetKeys = keys;
        MainContent.Visibility = Visibility.Hidden;
        Sheet.Visibility = Visibility.Visible;
        SheetScroll.ScrollToTop();
        Sheet.Focus();
        Keyboard.Focus(Sheet);
    }

    private void CloseSheet()
    {
        if (Sheet.Visibility != Visibility.Visible) return;
        Sheet.Visibility = Visibility.Collapsed;
        MainContent.Visibility = Visibility.Visible;
        _sheetKeys = null;
        FocusLedger();
    }

    private FrameworkElement Line(string text, bool inverse = false, bool dim = false, bool blink = false, double size = 20, Panel? into = null)
    {
        into ??= SheetBody;
        var tb = new TextBlock
        {
            Text = text,
            Style = (Style)FindResource(inverse ? "InverseText" : blink && !IsPlain ? "Blink" : dim ? "TermDim" : "Term"),
            FontSize = IsPlain ? App.PlainTextSize : size,
            TextWrapping = TextWrapping.Wrap,
        };
        blink = blink && !IsPlain;
        FrameworkElement el = tb;
        if (inverse)
        {
            var border = new Border { Style = (Style)FindResource("InverseBorder"), Child = tb, HorizontalAlignment = HorizontalAlignment.Left, Margin = new Thickness(0, 1, 0, 1) };
            if (blink) border.BeginStoryboard((Storyboard)FindResource("BlinkStoryboard"));
            el = border;
        }

        into.Children.Add(el);
        return el;
    }

    private void Gap(double h = 10) => SheetBody.Children.Add(new Border { Height = h });

    private void Prompt(string text, bool blinkCursor = true)
    {
        var panel = new StackPanel { Orientation = Orientation.Horizontal };
        panel.Children.Add(new TextBlock { Text = text, Style = (Style)FindResource("Term") });
        if (blinkCursor && !IsPlain) panel.Children.Add(new TextBlock { Text = " █", Style = (Style)FindResource("Blink") });
        SheetPrompt.Children.Add(panel);
    }

    // ───────────────────────────── Screens ─────────────────────────────

    private void ShowHelp()
    {
        ShowSheet("HELP", k =>
        {
            if (k is Key.Escape or Key.Enter or Key.F1) { CloseSheet(); return true; }
            return false;
        });

        Line("WASM CHASM REMOVES THE WASM DATA OF SELECTED AIRCRAFT FROM MICROSOFT FLIGHT SIMULATOR 2024.");
        Line("SUPPORT TEAMS OFTEN ASK FOR THIS TO FIX CRASHES OR CORRUPT STATE.", dim: true);
        Gap();
        foreach (var (k, d) in new[]
                 {
                     ("UP / DOWN", "MOVE THE CURSOR"),
                     ("SPACE", "MARK OR UNMARK THE AIRCRAFT UNDER THE CURSOR (CLICK ALSO WORKS)"),
                     ("RIGHT / LEFT", "MARK / UNMARK THE AIRCRAFT UNDER THE CURSOR"),
                     ("CTRL+A", "MARK EVERY AIRCRAFT SHOWN"),
                     ("CTRL+D / ESC", "UNMARK ALL"),
                     ("/ OR CTRL+F", "FILTER BY NAME"),
                     ("F6 / SHIFT+F6", "CHANGE SORT: NAME, SIZE, DATE / REVERSE"),
                     ("ENTER", "PURGE MARKED AIRCRAFT (YOU CONFIRM FIRST)"),
                     ("F2", "SWITCH INSTALL (STEAM / STORE)"),
                     ("F3", "SETTINGS: DISPOSAL METHOD, BACKUPS, SCOPE, DRY RUN, CRT EFFECTS, SOUND"),
                     ("F4", "SWITCH BETWEEN FUN AND SERIOUS MODE (NO EFFECTS, SAME FUNCTIONS)"),
                     ("F5", "RESCAN"),
                 })
        {
            Line($"{k,-14} {d}");
        }

        Gap();
        Line("BY DEFAULT ONLY THE COMPILED FILES THE SIM GENERATED ARE REMOVED, AND THEY GO TO THE RECYCLE BIN. SETTINGS ARE KEPT.");
        Line("NOTHING OUTSIDE THE WASM FOLDER IS EVER TOUCHED. NO ADMIN RIGHTS. NO NETWORK ACCESS.");
        Line("A LOG OF EVERY SESSION IS KEPT IN " + SessionLog.DefaultDirectory.ToUpperInvariant(), dim: true);
        Gap();
        Line("NOT AFFILIATED WITH MICROSOFT OR ASOBO STUDIO. MIT LICENCE. FONT: VT323 (SIL OFL 1.1).", dim: true);
        Prompt("ENTER OR ESC RETURNS.");
    }

    private void ShowNoInstall()
    {
        ShowSheet("NO INSTALL FOUND", k =>
        {
            switch (k)
            {
                case Key.F5: CloseSheet(); DetectAndChoose(); return true;
                case Key.Escape: Close(); return true;
            }

            return false;
        });

        Line("NO MICROSOFT FLIGHT SIMULATOR 2024 INSTALL WAS FOUND. A WASM FOLDER AND USERCFG.OPT WERE EXPECTED TOGETHER AT ONE OF:");
        Gap();
        foreach (var store in _config.Stores)
        {
            Line(store.Label);
            Line("  " + (PathTemplate.Expand(store.WasmRoot, Environment.GetEnvironmentVariable) ?? store.WasmRoot), dim: true, size: 18);
        }

        Gap();
        Line("START THE SIMULATOR ONCE IF IT HAS NEVER RUN ON THIS PC, THEN TRY AGAIN.");
        Prompt("F5: TRY AGAIN    ESC: QUIT");
    }

    private void ShowInstallPicker(bool firstRun)
    {
        _detected = InstallDetection.DetectAll(InstallDetection.CreateLocators(_config));
        var options = _detected.ToList();

        ShowSheet(firstRun ? "SELECT INSTALL" : "SWITCH INSTALL", k =>
        {
            int idx = k switch
            {
                >= Key.D1 and <= Key.D9 => k - Key.D1,
                >= Key.NumPad1 and <= Key.NumPad9 => k - Key.NumPad1,
                _ => -1,
            };
            if (idx >= 0 && idx < options.Count) { UseInstall(options[idx]); return true; }
            if (k is Key.Escape or Key.F2 && _install is not null) { CloseSheet(); return true; }
            return false;
        });

        if (firstRun)
        {
            Line("BOTH STEAM AND MICROSOFT STORE INSTALLS WERE FOUND ON THIS MACHINE.");
            Line("CHOOSE ONE. THE CHOICE IS REMEMBERED AND CAN BE CHANGED WITH F2.", dim: true);
            Gap();
        }

        for (int i = 0; i < options.Count; i++)
        {
            var o = options[i];
            bool current = _install is not null && string.Equals(_install.WasmRoot, o.WasmRoot, StringComparison.OrdinalIgnoreCase);
            Line($"{i + 1}  {o.Label}{(current ? "  (CURRENT)" : "")}", inverse: current);
            Line("   " + o.WasmRoot, dim: true, size: 18);
        }

        if (options.Count == 0) Line("NO INSTALLS DETECTED.");
        Gap();
        Prompt(_install is null ? "PRESS A NUMBER." : "PRESS A NUMBER.    ESC: CANCEL");
    }

    private int _settingsCursor;

    private void ShowSettings()
    {
        const int count = 6;
        ShowSheet("SETTINGS", k =>
        {
            switch (k)
            {
                case Key.Up: _settingsCursor = (_settingsCursor + count - 1) % count; _sound.PlayTick(); RenderSettings(); return true;
                case Key.Down: _settingsCursor = (_settingsCursor + 1) % count; _sound.PlayTick(); RenderSettings(); return true;
                case Key.Space or Key.Enter or Key.Left or Key.Right: ActivateSetting(_settingsCursor); RenderSettings(); return true;
                case Key.Escape or Key.F3:
                    _settings.Save();
                    ApplyCrt();
                    _sound.Enabled = _settings.Sound && !IsPlain;
                    foreach (var r in _rows) r.Scope = _settings.Scope;
                    SizeHeading.Text = ScopeText.ColumnHeading(_settings.Scope);
                    UpdateTotals();
                    CloseSheet();
                    _ = RescanAsync();
                    return true;
            }

            return false;
        });
        RenderSettings();
    }

    private void RenderSettings()
    {
        SheetBody.Children.Clear();
        SheetPrompt.Children.Clear();
        string mode = _settings.DisposalMode switch
        {
            DisposalMode.ZipThenRecycle => "ZIP BACKUP, THEN RECYCLE BIN",
            DisposalMode.Permanent => "PERMANENT DELETE (CANNOT BE UNDONE)",
            _ => "RECYCLE BIN",
        };
        var items = new[]
        {
            $"CRT EFFECTS ........ {(_settings.CrtEffects ? "ON" : "OFF")}{(IsPlain ? "  (NOT USED IN SERIOUS MODE)" : SystemParameters.HighContrast ? "  (OFF WHILE HIGH CONTRAST IS ACTIVE)" : "")}",
            $"SOUND .............. {(_settings.Sound ? "ON" : "OFF")}{(IsPlain ? "  (NOT USED IN SERIOUS MODE)" : "")}",
            $"DISPOSAL METHOD .... {mode}",
            $"BACKUP FOLDER ...... {(_settings.BackupFolder ?? "NOT SET")}",
            $"SCOPE .............. {ScopeText.Name(_settings.Scope)}",
            $"DRY RUN ............ {(_settings.DryRun ? "ON (NOTHING IS TOUCHED)" : "OFF")}",
        };

        for (int i = 0; i < items.Length; i++)
            Line((i == _settingsCursor ? "▶ " : "  ") + items[i], inverse: i == _settingsCursor);

        Gap();
        string note = _settingsCursor switch
        {
            0 => "SCANLINES, GLOW, CURVATURE AND THE ROLL BAND. TURN OFF FOR ACCESSIBILITY OR LOW-END GRAPHICS.",
            1 => "GENTLE HUM, GRADUAL POWER-UP, SOFT BLIP ON ROW CHANGE, RISING RATCHET ON SELECT, CRUMBLE IN THE CHASM, SOFT CHIME WHEN DONE.",
            2 => "RECYCLE BIN IS THE DEFAULT. A ZIP BACKUP IS WRITTEN TO THE BACKUP FOLDER FIRST IF CHOSEN.",
            3 => "WHERE ZIP BACKUPS ARE WRITTEN. ENTER TO CHOOSE.",
            4 => "COMPILED CACHE ONLY IS RECOMMENDED AND KEEPS SETTINGS. WORK FOLDER AND WHOLE PACKAGE ERASE SETTINGS AND ADD-ON DATA, WHICH MAY INCLUDE LICENCE FILES.",
            _ => "SHOWS WHAT WOULD BE PURGED, FALL INTO THE CHASM INCLUDED, AND TOUCHES NOTHING.",
        };
        Line(note, dim: true);
        Prompt("UP/DOWN MOVE.  SPACE OR ENTER CHANGES.  ESC SAVES AND RETURNS.");
    }

    private void ActivateSetting(int index)
    {
        switch (index)
        {
            case 0: _settings.CrtEffects = !_settings.CrtEffects; ApplyCrt(); break;
            case 1: _settings.Sound = !_settings.Sound; _sound.Enabled = _settings.Sound && !IsPlain; _sound.PlayClick(); break;
            case 2:
                var next = _settings.DisposalMode switch
                {
                    DisposalMode.RecycleBin => DisposalMode.ZipThenRecycle,
                    DisposalMode.ZipThenRecycle => DisposalMode.Permanent,
                    _ => DisposalMode.RecycleBin,
                };
                if (next == DisposalMode.ZipThenRecycle && string.IsNullOrEmpty(_settings.BackupFolder) && ChooseBackupFolder() is null)
                    next = DisposalMode.Permanent;
                _settings.DisposalMode = next;
                break;
            case 3: ChooseBackupFolder(); break;
            case 4:
                _settings.Scope = _settings.Scope switch
                {
                    DeletionScope.CompiledCache => DeletionScope.WorkFolder,
                    DeletionScope.WorkFolder => DeletionScope.PackageFolder,
                    _ => DeletionScope.CompiledCache,
                };
                break;
            case 5: _settings.DryRun = !_settings.DryRun; break;
        }
    }

    private string? ChooseBackupFolder()
    {
        var dlg = new OpenFolderDialog { Title = "Choose a folder for WASM Chasm backups" };
        if (!string.IsNullOrEmpty(_settings.BackupFolder) && Directory.Exists(_settings.BackupFolder))
            dlg.InitialDirectory = _settings.BackupFolder;
        if (dlg.ShowDialog(this) != true) return null;

        var folder = Path.GetFullPath(dlg.FolderName);
        // A backup inside the WASM root would be swept up by a later consignment.
        if (_install is not null && folder.StartsWith(_install.WasmRoot, StringComparison.OrdinalIgnoreCase))
        {
            StatusText.Text = "BACKUP FOLDER CANNOT BE INSIDE THE WASM FOLDER.";
            return null;
        }

        _settings.BackupFolder = folder;
        _settings.Save();
        return folder;
    }

    // ───────────────────────────── Consignment ─────────────────────────────

    private ConsignmentService CreateService() =>
        new(Catalog.GuardFor(_install!), new SimProcessDetector(_config.SimProcessNames), _log, _recycleBin);

    private void BeginConsign()
    {
        if (_install is null) return;
        var groups = _rows.Where(r => r.IsMarked && !r.IsDetached).Select(r => r.Group).ToList();
        if (groups.Count == 0)
        {
            StatusText.Text = "NO AIRCRAFT MARKED. SPACE MARKS THE AIRCRAFT UNDER THE CURSOR.";
            return;
        }

        ShowConfirm(groups, includeUnknown: false);
    }

    private void ShowConfirm(IReadOnlyList<AircraftGroup> groups, bool includeUnknown)
    {
        var options = new ConsignOptions { Scope = _settings.Scope, DryRun = _settings.DryRun, IncludeUnknownSimVersions = includeUnknown };
        var targets = ConsignmentService.BuildTargets(groups, options);
        var excludedUnknown = includeUnknown ? new List<WasmEntry>() : groups.SelectMany(g => g.Entries).Where(e => !e.IsKnownSimVersion).ToList();
        var aircraft = targets.GroupBy(t => t.PackageId, StringComparer.OrdinalIgnoreCase)
            .Select(g => (Name: g.First().DisplayName, Bytes: g.Sum(t => t.SizeBytes)))
            .ToList();
        int nothingToPurge = groups.Count - aircraft.Count;
        bool needY = aircraft.Count > 5;
        TextBox? yBox = null;

        string heading = targets.Count == 0 ? "NOTHING TO PURGE"
            : _settings.DryRun ? $"DRY RUN: PURGE {aircraft.Count} AIRCRAFT?"
            : $"PURGE {aircraft.Count} AIRCRAFT?";

        ShowSheet(heading, k =>
        {
            switch (k)
            {
                case Key.Escape:
                    CloseSheet();
                    return true;
                case Key.U when excludedUnknown.Count > 0:
                    ShowConfirm(groups, includeUnknown: true);
                    return true;
                case Key.Enter when targets.Count > 0:
                    if (needY && !string.Equals(yBox?.Text.Trim(), "Y", StringComparison.OrdinalIgnoreCase))
                    {
                        yBox?.Focus();
                        return true;
                    }

                    RunPreflight(targets, options, _settings.DryRun ? DisposalMode.Permanent : _settings.DisposalMode);
                    return true;
            }

            return false;
        });

        var size = ByteFormat.Megabytes(targets.Sum(t => t.SizeBytes));
        bool permanent = !_settings.DryRun && _settings.DisposalMode == DisposalMode.Permanent;
        if (targets.Count > 0)
        {
            if (_settings.DryRun) Line("DRY RUN. NOTHING WILL BE TOUCHED.");
            else if (permanent) Line($"{size} WILL BE DELETED FOREVER. THIS CANNOT BE UNDONE.", inverse: true);
            else if (_settings.DisposalMode == DisposalMode.ZipThenRecycle) Line($"{size} WILL BE BACKED UP, THEN MOVED TO THE RECYCLE BIN.");
            else Line($"{size} WILL MOVE TO THE RECYCLE BIN.");

            // The default scope is safe, so it stays calm; the blinking warning is kept for the scopes that erase settings.
            if (_settings.Scope != DeletionScope.CompiledCache) Line("WARNING", inverse: true, blink: true);
            Line(ScopeText.Warning(_settings.Scope).ToUpperInvariant());
            Gap(12);
            foreach (var (name, bytes) in aircraft) Line($"  {name,-58} {ByteFormat.Format(bytes),10}");
        }

        if (nothingToPurge > 0)
        {
            Gap(6);
            Line($"{nothingToPurge} MARKED AIRCRAFT HAVE NOTHING TO PURGE.", dim: true);
        }

        if (excludedUnknown.Count > 0)
        {
            Gap(6);
            Line($"{excludedUnknown.Count} UNKNOWN FOLDERS NOT INCLUDED.  U: INCLUDE THEM", dim: true);
        }

        if (targets.Count == 0)
        {
            Prompt("ESC: BACK");
            return;
        }

        if (needY)
        {
            var panel = new StackPanel { Orientation = Orientation.Horizontal };
            panel.Children.Add(new TextBlock { Text = "MORE THAN FIVE AIRCRAFT. TYPE Y, THEN ENTER.    ESC: CANCEL    ", Style = (Style)FindResource("Term") });
            yBox = new TextBox { Style = (Style)FindResource("TermTextBox"), Width = 30, MaxLength = 1 };
            AutomationPropertiesHelper.SetName(yBox, "Type Y to confirm");
            panel.Children.Add(yBox);
            SheetPrompt.Children.Add(panel);
            Dispatcher.BeginInvoke(DispatcherPriority.Input, () => yBox.Focus());
        }
        else
        {
            Prompt("ENTER: PURGE    ESC: CANCEL");
        }
    }

    private void RunPreflight(IReadOnlyList<ConsignTarget> targets, ConsignOptions options, DisposalMode mode)
    {
        var service = CreateService();
        var pre = service.Preflight(targets, mode);

        switch (pre.Problem)
        {
            case PreflightProblem.SimRunning:
                ShowSheet("SIMULATOR RUNNING", k =>
                {
                    if (k == Key.Enter) { RunPreflight(targets, options, mode); return true; }
                    if (k == Key.Escape) { CloseSheet(); return true; }
                    return false;
                });
                Line("FLIGHTSIMULATOR2024.EXE IS RUNNING. ITS DATA CANNOT BE PURGED WHILE IT IS OPEN.");
                Line("NOTHING HAS BEEN TOUCHED.", dim: true);
                Prompt("CLOSE THE SIMULATOR, THEN PRESS ENTER TO TRY AGAIN.  ESC CANCELS.");
                return;

            case PreflightProblem.NothingToConsign:
                ShowSheet("NOTHING TO PURGE", k =>
                {
                    if (k is Key.Escape or Key.Enter) { CloseSheet(); return true; }
                    return false;
                });
                Line(pre.Message);
                foreach (var r in pre.Refused) Line($"  {r.Target.Label}: {r.Reason}", dim: true, size: 18);
                Prompt("ESC RETURNS.");
                return;

            case PreflightProblem.RecycleBinUnavailable:
                ShowSheet("RECYCLE BIN UNAVAILABLE", k =>
                {
                    if (k == Key.Escape) { CloseSheet(); return true; }
                    if (k == Key.Z)
                    {
                        if (string.IsNullOrEmpty(_settings.BackupFolder) || !Directory.Exists(_settings.BackupFolder))
                        {
                            if (ChooseBackupFolder() is null) return true;
                        }

                        _ = ExecuteAsync(service, pre, options, DisposalMode.ZipThenPermanent);
                        return true;
                    }

                    return false;
                });
                Line("THESE ITEMS CANNOT GO TO THE RECYCLE BIN:");
                Line(pre.Message.ToUpperInvariant(), dim: true);
                Gap();
                Line("WASM CHASM NEVER DELETES PERMANENTLY WITHOUT ASKING. CHOOSE:");
                Line($"Z    WRITE A ZIP BACKUP TO {(_settings.BackupFolder ?? "A FOLDER YOU CHOOSE").ToUpperInvariant()}, THEN DELETE PERMANENTLY");
                Line("ESC  CANCEL. NOTHING WILL BE TOUCHED.");
                Prompt("Z OR ESC.");
                return;

            default:
                _ = ExecuteAsync(service, pre, options, mode);
                return;
        }
    }

    private async Task ExecuteAsync(ConsignmentService service, PreflightResult pre, ConsignOptions options, DisposalMode mode)
    {
        IDisposalStrategy strategy;
        try
        {
            strategy = options.DryRun ? new PermanentStrategy() : DisposalStrategies.Create(mode, _recycleBin, _settings.BackupFolder);
        }
        catch (ArgumentException ex)
        {
            CloseSheet();
            StatusText.Text = ex.Message.ToUpperInvariant();
            return;
        }

        CloseSheet();
        _consigning = true;
        Ledger.IsHitTestVisible = false;
        Legend.IsEnabled = false;
        _consignCts = new CancellationTokenSource();
        StatusText.Text = options.DryRun ? "DRY RUN. NOTHING IS BEING PURGED." : "PURGING. ESC STOPS AFTER THE CURRENT FOLDER.";
        _sound.StartHum();

        var remaining = pre.Targets.GroupBy(t => t.PackageId, StringComparer.OrdinalIgnoreCase)
            .ToDictionary(g => g.Key, g => g.Count(), StringComparer.OrdinalIgnoreCase);
        var removedAny = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        var progress = new Progress<ConsignProgress>(p =>
        {
            var id = p.Result.Target.PackageId;
            // A dry run rehearses the fall too. Nothing is touched, and the rescan after the results screen brings the rows back.
            if (p.Result.Outcome is ConsignOutcome.Removed or ConsignOutcome.WouldRemove) removedAny.Add(id);
            if (!remaining.ContainsKey(id)) return;
            if (--remaining[id] > 0) return;
            var row = _rows.FirstOrDefault(r => string.Equals(r.PackageId, id, StringComparison.OrdinalIgnoreCase));
            if (row is not null) EnqueueFall(row, removedAny.Contains(id));
            StatusText.Text = $"{(options.DryRun ? "DRY RUN. NOTHING IS BEING PURGED. " : "")}{p.Index + 1} OF {p.Total} FOLDERS PROCESSED.";
        });

        IReadOnlyList<ConsignResult> results;
        try
        {
            var token = _consignCts.Token;
            results = await Task.Run(() => service.Execute(pre.Targets, strategy, options, progress, token));
        }
        catch (Exception ex)
        {
            results = pre.Targets.Select(t => new ConsignResult(t, ConsignOutcome.Failed, ex.Message)).ToList();
        }

        // Let queued progress callbacks run, then let the last rows finish falling.
        await Dispatcher.InvokeAsync(() => { }, DispatcherPriority.Background);
        await _animationTail;

        _sound.StopHum();
        _sound.PlayChime();
        _consigning = false;
        Ledger.IsHitTestVisible = true;
        Legend.IsEnabled = true;
        _consignCts.Dispose();
        _consignCts = null;

        // In Retro, watching a clean purge fall into the chasm is the confirmation. Anything that needs
        // reading still gets the page, and so does a run with no animation to watch.
        var all = pre.Refused.Concat(results).ToList();
        if (IsPlain || options.DryRun || !SystemParameters.ClientAreaAnimation || ScopeText.NeedsResultsPage(all))
        {
            ShowResults(all, options.DryRun);
        }
        else
        {
            await RescanAsync();
            StatusText.Text = ScopeText.Summary(all, dryRun: false);
        }
    }

    private void ShowResults(IReadOnlyList<ConsignResult> results, bool dryRun)
    {
        ShowSheet(dryRun ? "DRY RUN DONE" : "DONE", k =>
        {
            if (k is Key.Enter or Key.Escape)
            {
                CloseSheet();
                _ = RescanAsync();
                return true;
            }

            return false;
        });

        int skipped = results.Count(r => r.Outcome == ConsignOutcome.Skipped);
        int failed = results.Count(r => r.Outcome == ConsignOutcome.Failed);

        Line(ScopeText.Summary(results, dryRun), size: 22);
        if (skipped + failed > 0) Line($"{skipped} SKIPPED. {failed} FAILED.", inverse: failed > 0);
        Gap();

        foreach (var r in results)
        {
            string tag = r.Outcome switch
            {
                ConsignOutcome.Removed => "PURGED",
                ConsignOutcome.WouldRemove => "WOULD PURGE",
                ConsignOutcome.Skipped => "SKIPPED",
                _ => "FAILED",
            };
            Line($"{tag,-12} {r.Target.DisplayName}  [{r.Target.SimVersionFolder}]  {ByteFormat.Format(r.Target.SizeBytes)}",
                inverse: r.Outcome == ConsignOutcome.Failed);
            Line($"             {r.Target.Label}", dim: true, size: 18);
            Line($"             {r.Reason.ToUpperInvariant()}", dim: r.Outcome != ConsignOutcome.Failed, size: 18);
            if (r.LinkOnly) Line("             LINK REMOVED. ITS TARGET WAS NOT TOUCHED.", size: 18);
            if (r.BackupPath is not null) Line($"             BACKUP: {r.BackupPath}", dim: true, size: 18);
        }

        Gap();
        Line("LOG: " + _log.FilePath, dim: true, size: 18);
        Prompt("ENTER OR ESC RETURNS TO THE LIST.");
    }

    // ───────────────────────────── The consign animation ─────────────────────────────

    /// <summary>Queues a row's fall. Rows fall one at a time, in the order their folders complete.</summary>
    private void EnqueueFall(AircraftRow row, bool removed)
    {
        _pendingAnimations++;
        _animationTail = FallAfter(_animationTail, row, removed);
    }

    private async Task FallAfter(Task previous, AircraftRow row, bool removed)
    {
        await previous;
        try { await AnimateFall(row, removed); }
        catch (Exception) { row.IsDetached = removed; }
        finally { _pendingAnimations--; UpdateTotals(); }
    }

    private async Task AnimateFall(AircraftRow row, bool removed)
    {
        if (!SystemParameters.ClientAreaAnimation || IsPlain)
        {
            row.IsDetached = removed;
            return;
        }

        double speed = _pendingAnimations > 2 ? 0.45 : 1.0;
        Ledger.ScrollIntoView(row);
        Ledger.UpdateLayout();

        var container = Ledger.ItemContainerGenerator.ContainerFromItem(row) as ListBoxItem;
        Point start = container is { IsVisible: true }
            ? container.TranslatePoint(new Point(18, 0), FxLayer)
            : Ledger.TranslatePoint(new Point(18, Ledger.ActualHeight / 2), FxLayer);

        var text = $"■ {row.Title}  {row.SizeText}";
        var label = new TextBlock { Text = text, Style = (Style)FindResource("InverseText") };
        var ghost = new Border { Style = (Style)FindResource("InverseBorder"), Child = label };
        FxLayer.Children.Add(ghost);
        Canvas.SetLeft(ghost, start.X);
        Canvas.SetTop(ghost, start.Y);
        ghost.Measure(new Size(double.PositiveInfinity, double.PositiveInfinity));
        var size = ghost.DesiredSize;

        if (!removed)
        {
            // Failed or skipped: the row refuses to fall. Flash it and put it back.
            var flash = new DoubleAnimationUsingKeyFrames { Duration = TimeSpan.FromSeconds(0.9) };
            for (int i = 0; i < 6; i++) flash.KeyFrames.Add(new DiscreteDoubleKeyFrame(i % 2 == 0 ? 0 : 1, KeyTime.FromTimeSpan(TimeSpan.FromSeconds(0.15 * i))));
            await Animate(ghost, OpacityProperty, flash);
            FxLayer.Children.Remove(ghost);
            return;
        }

        row.IsDetached = true;
        UpdateTotals();

        // 1. Detach and slide right to the mouth of the pit, shrinking to fit.
        var mouth = Chasm.TranslatePoint(Chasm.MouthPoint, FxLayer);
        double scale = Math.Min(1.0, Chasm.MouthHalfWidth * 1.8 / Math.Max(1, size.Width));
        var st = new ScaleTransform(1, 1);
        ghost.RenderTransform = st;
        var dur = TimeSpan.FromSeconds(0.5 * speed);
        var ease = new QuadraticEase { EasingMode = EasingMode.EaseInOut };
        await Task.WhenAll(
            Animate(ghost, Canvas.LeftProperty, new DoubleAnimation(start.X, mouth.X - size.Width * scale / 2, dur) { EasingFunction = ease }),
            Animate(ghost, Canvas.TopProperty, new DoubleAnimation(start.Y, mouth.Y - size.Height * scale - 2, dur) { EasingFunction = ease }),
            Animate(st, ScaleTransform.ScaleXProperty, new DoubleAnimation(1, scale, dur)),
            Animate(st, ScaleTransform.ScaleYProperty, new DoubleAnimation(1, scale, dur)));

        // 2. Break into characters that fall into the dark.
        _sound.PlayDisintegrate();
        double x0 = Canvas.GetLeft(ghost) + 6 * scale;
        double y0 = Canvas.GetTop(ghost);
        double charW = label.DesiredSize.Width > 0 ? label.DesiredSize.Width / text.Length : 9;
        FxLayer.Children.Remove(ghost);

        var floor = Chasm.TranslatePoint(Chasm.FloorPoint, FxLayer);
        var falls = new List<Task>();
        var phosphor = (Brush)FindResource("Phosphor");
        for (int i = 0; i < text.Length; i++)
        {
            if (char.IsWhiteSpace(text[i])) continue;
            var ch = new TextBlock
            {
                Text = text[i].ToString(),
                FontFamily = (FontFamily)FindResource("TerminalFont"),
                FontSize = 20 * scale,
                Foreground = phosphor,
            };
            double x = x0 + i * charW * scale;
            Canvas.SetLeft(ch, x);
            Canvas.SetTop(ch, y0);
            var tt = new TranslateTransform();
            var sc = new ScaleTransform(1, 1);
            ch.RenderTransform = new TransformGroup { Children = { sc, tt } };
            FxLayer.Children.Add(ch);

            var begin = TimeSpan.FromSeconds(_rng.NextDouble() * 0.25 * speed);
            var fall = TimeSpan.FromSeconds((0.7 + _rng.NextDouble() * 0.45) * speed);
            var fallEase = new QuadraticEase { EasingMode = EasingMode.EaseIn };
            double dy = (floor.Y - y0) * (0.55 + _rng.NextDouble() * 0.45);
            double dx = (mouth.X - x) * (0.5 + _rng.NextDouble() * 0.4);

            falls.Add(Animate(tt, TranslateTransform.YProperty, new DoubleAnimation(0, dy, fall) { BeginTime = begin, EasingFunction = fallEase }));
            falls.Add(Animate(tt, TranslateTransform.XProperty, new DoubleAnimation(0, dx, fall) { BeginTime = begin, EasingFunction = fallEase }));
            falls.Add(Animate(sc, ScaleTransform.ScaleXProperty, new DoubleAnimation(1, 0.3, fall) { BeginTime = begin }));
            falls.Add(Animate(sc, ScaleTransform.ScaleYProperty, new DoubleAnimation(1, 0.3, fall) { BeginTime = begin }));
            falls.Add(Animate(ch, OpacityProperty, new DoubleAnimation(1, 0, fall) { BeginTime = begin, EasingFunction = fallEase }));
        }

        await Task.WhenAll(falls);
        foreach (var el in FxLayer.Children.OfType<TextBlock>().ToList()) FxLayer.Children.Remove(el);
    }

    private static Task Animate(IAnimatable target, DependencyProperty property, AnimationTimeline animation)
    {
        var tcs = new TaskCompletionSource();
        animation.Completed += (_, _) => tcs.TrySetResult();
        target.BeginAnimation(property, animation);
        return tcs.Task;
    }

    // ───────────────────────────── CRT and chrome ─────────────────────────────

    // ───────────────────────────── Retro / Plain ─────────────────────────────

    private bool IsPlain => _settings.Mode == AppMode.Plain;
    private Brush? _retroBezel;
    private Brush? _retroFrame;
    private Geometry? _retroClip;
    private Brush? _retroWindow;

    private void ToggleMode() => SetMode(IsPlain ? AppMode.Retro : AppMode.Plain);

    private void SetMode(AppMode mode)
    {
        _settings.Mode = mode;
        _settings.Save();
        ApplyMode();
        ApplyCrt();
        Chasm.Redraw();
    }

    /// <summary>
    /// Plain mode is the same app with the theatre removed: no bezel, chasm, effects, animations or sound.
    /// Only appearance changes here; everything the app does to files is untouched.
    /// </summary>
    private void ApplyMode()
    {
        bool plain = IsPlain;
        App.SetPlainTheme(plain);

        _retroBezel ??= Bezel.Background;
        _retroFrame ??= ScreenFrame.Background;
        _retroClip ??= ScreenRoot.Clip;
        _retroWindow ??= Background;

        Background = plain ? (Brush)FindResource("ScreenBg") : _retroWindow;
        Monitor.Margin = new Thickness(plain ? 0 : 12);
        Bezel.Background = plain ? Brushes.Transparent : _retroBezel;
        Bezel.BorderThickness = new Thickness(plain ? 0 : 2);
        ScreenFrame.Background = plain ? Brushes.Transparent : _retroFrame;
        ScreenFrame.BorderThickness = new Thickness(plain ? 0 : 6);
        ScreenFrame.Margin = plain ? new Thickness(0) : new Thickness(34, 30, 34, 50);
        ScreenRoot.Width = plain ? 1040 : 952;
        ScreenRoot.Height = plain ? 800 : 708;
        ScreenRoot.Clip = plain ? null : _retroClip;

        var furniture = plain ? Visibility.Collapsed : Visibility.Visible;
        MakerPlate.Visibility = furniture;
        ClockText.Visibility = furniture;
        PowerPanel.Visibility = furniture;
        Chasm.Visibility = furniture;
        ChasmGapColumn.Width = new GridLength(plain ? 0 : 16);
        ChasmColumn.Width = plain ? new GridLength(0) : new GridLength(1, GridUnitType.Star);

        ModeText.Text = plain ? "MODE: SERIOUS  [F4]" : "MODE: FUN  [F4]";
        _sound.Enabled = _settings.Sound && !plain;
    }

    // ───────────────────────────── Splash ─────────────────────────────

    private const double SplashSeconds = 2.6;
    private bool _splashUp;
    private DispatcherTimer? _splashTimer;

    /// <summary>
    /// The logo, shown through the power-on effect. The scan carries on behind it, and any key or click skips it.
    /// Skipped entirely when Windows animations are off.
    /// </summary>
    private void PrepareSplash()
    {
        if (!SystemParameters.ClientAreaAnimation) return;

        SplashWasm.OpacityMask = SplashStripes();
        SplashChasm.OpacityMask = SplashStripes();
        _splashUp = true;
        Splash.Visibility = Visibility.Visible;
        MainContent.Opacity = 0;
        Sheet.Opacity = 0;

        void Start()
        {
            if (!_splashUp) return;
            _splashTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(SplashSeconds) };
            _splashTimer.Tick += (_, _) => DismissSplash();
            _splashTimer.Start();
        }

        WhenRendered(Start);
    }

    /// <summary>Solid at the top of the letters, then cut by stripes that widen towards the bottom.</summary>
    private static Brush SplashStripes()
    {
        var mask = new LinearGradientBrush { StartPoint = new Point(0, 0), EndPoint = new Point(0, 1) };
        void Band(double from, double to, Color color)
        {
            mask.GradientStops.Add(new GradientStop(color, from));
            mask.GradientStops.Add(new GradientStop(color, to));
        }

        Band(0, 0.56, Colors.Black);
        double y = 0.56;
        double gap = 0.008;
        double solid = 0.04;
        while (y < 1)
        {
            Band(y, Math.Min(1, y + gap), Colors.Transparent);
            y += gap;
            Band(y, Math.Min(1, y + solid), Colors.Black);
            y += solid;
            gap += 0.006;
            solid = Math.Max(0.016, solid - 0.004);
        }

        return mask;
    }

    private void OnSplashMouseDown(object sender, MouseButtonEventArgs e)
    {
        DismissSplash();
        e.Handled = true;
    }

    private void DismissSplash()
    {
        if (!_splashUp) return;
        _splashUp = false;
        _splashTimer?.Stop();
        Sheet.Opacity = 1;

        var fade = TimeSpan.FromSeconds(0.25);
        MainContent.BeginAnimation(OpacityProperty, new DoubleAnimation(0, 1, fade) { FillBehavior = FillBehavior.Stop });
        MainContent.Opacity = 1;
        var fadeOut = new DoubleAnimation(1, 0, fade) { FillBehavior = FillBehavior.Stop };
        fadeOut.Completed += (_, _) => Splash.Visibility = Visibility.Collapsed;
        Splash.IsHitTestVisible = false;
        Splash.BeginAnimation(OpacityProperty, fadeOut);
    }

    // ───────────────────────────── Power-on ─────────────────────────────

    /// <summary>
    /// A short tube warm-up: a bright line grows across the dark screen, opens to full height, and the display
    /// flickers on. It is cosmetic, so it never blocks input, and it respects the settings that turn effects off.
    /// </summary>
    private void PreparePowerOn()
    {
        bool on = _settings.CrtEffects && !SystemParameters.HighContrast && SystemParameters.ClientAreaAnimation;
        if (!on) return;

        var contentScale = new ScaleTransform(1, 0.01);
        var beamScale = new ScaleTransform(0, 3.0 / 708);
        Glow.RenderTransformOrigin = new Point(0.5, 0.5);
        Glow.RenderTransform = contentScale;
        Glow.Opacity = 0;
        PowerBeam.RenderTransform = beamScale;
        PowerBeam.Visibility = Visibility.Visible;
        PowerLamp.Opacity = 0;

        void Start()
        {
            _sound.PlayPowerOn();

            static DoubleAnimationUsingKeyFrames Keys(params (double Seconds, double Value)[] frames)
            {
                var a = new DoubleAnimationUsingKeyFrames();
                foreach (var (s, v) in frames)
                    a.KeyFrames.Add(new EasingDoubleKeyFrame(v, KeyTime.FromTimeSpan(TimeSpan.FromSeconds(s)), new QuadraticEase { EasingMode = EasingMode.EaseOut }));
                return a;
            }

            contentScale.BeginAnimation(ScaleTransform.ScaleYProperty, Keys((0.14, 0.01), (0.38, 1)));
            Glow.BeginAnimation(OpacityProperty, Keys((0.20, 0), (0.42, 1), (0.50, 0.65), (0.60, 1), (0.66, 0.85), (0.74, 1)));
            beamScale.BeginAnimation(ScaleTransform.ScaleXProperty, Keys((0.14, 1)));
            beamScale.BeginAnimation(ScaleTransform.ScaleYProperty, Keys((0.14, 3.0 / 708), (0.36, 1)));
            PowerLamp.BeginAnimation(OpacityProperty, Keys((0.05, 0.85)));

            var fade = Keys((0.14, 0.95), (0.36, 0.45), (0.85, 0));
            fade.Completed += (_, _) =>
            {
                // Remove the transform as well as the animation, so text stays crisp afterwards.
                PowerBeam.BeginAnimation(OpacityProperty, null);
                PowerBeam.Visibility = Visibility.Collapsed;
                Glow.BeginAnimation(OpacityProperty, null);
                Glow.Opacity = 1;
                Glow.RenderTransform = null;
                PowerLamp.BeginAnimation(OpacityProperty, null);
                PowerLamp.Opacity = 0.85;
            };
            PowerBeam.BeginAnimation(OpacityProperty, fade);
        }

        WhenRendered(Start);
    }

    private void ApplyCrt()
    {
        bool on = _settings.CrtEffects && !SystemParameters.HighContrast && !IsPlain;
        if (on)
        {
            var shader = CrtEffect.TryCreate();
            ScreenRoot.Effect = shader;
            ScanlineOverlay.Visibility = shader is null ? Visibility.Visible : Visibility.Collapsed;
            VignetteOverlay.Visibility = shader is null ? Visibility.Visible : Visibility.Collapsed;
            GlassOverlay.Visibility = Visibility.Visible;
            RollBand.Visibility = Visibility.Visible;
            Glow.Effect = new DropShadowEffect
            {
                Color = Color.FromRgb(0xFF, 0xB0, 0x00),
                BlurRadius = 8,
                ShadowDepth = 0,
                Opacity = 0.55,
            };
            RollTransform.BeginAnimation(TranslateTransform.YProperty,
                new DoubleAnimation(-0.1695, 1, TimeSpan.FromSeconds(9)) { RepeatBehavior = RepeatBehavior.Forever });
            Chasm.AnimateTitle(true);
        }
        else
        {
            Chasm.AnimateTitle(false);
            ScreenRoot.Effect = null;
            Glow.Effect = null;
            ScanlineOverlay.Visibility = Visibility.Collapsed;
            VignetteOverlay.Visibility = Visibility.Collapsed;
            GlassOverlay.Visibility = Visibility.Collapsed;
            RollBand.Visibility = Visibility.Collapsed;
            RollTransform.BeginAnimation(TranslateTransform.YProperty, null);
        }
    }

    private void UpdateClock()
    {
        var elapsed = DateTime.Now - _sessionStart;
        ClockText.Text = $"SESSION {(int)elapsed.TotalHours:00}:{elapsed.Minutes:00}:{elapsed.Seconds:00}  {DateTime.Now:HH:mm}";
    }

    private void OnSwitchInstallClick(object sender, RoutedEventArgs e)
    {
        if (!_consigning && Sheet.Visibility != Visibility.Visible) ShowInstallPicker(firstRun: false);
    }
}

internal static class AutomationPropertiesHelper
{
    public static void SetName(DependencyObject element, string name) =>
        System.Windows.Automation.AutomationProperties.SetName(element, name);
}
