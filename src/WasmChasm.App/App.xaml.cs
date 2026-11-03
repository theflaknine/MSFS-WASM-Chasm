using System.Windows;
using System.Windows.Media;
using System.Windows.Threading;
using WasmChasm.Core;

namespace WasmChasm.App;

public partial class App : Application
{
    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);
        DispatcherUnhandledException += OnUnhandled;
        ApplyTheme();
        SystemParameters.StaticPropertyChanged += (_, args) =>
        {
            if (args.PropertyName == nameof(SystemParameters.HighContrast)) ApplyTheme();
        };

        new MainWindow().Show();
    }

    /// <summary>Plain mode uses this one size for all text.</summary>
    public const double PlainTextSize = 18;

    private static bool _plain;
    private static FontFamily? _retroFont;

    /// <summary>Plain mode: plain grey with a standard font. Windows high contrast still wins on colour.</summary>
    public static void SetPlainTheme(bool plain)
    {
        _plain = plain;
        ApplyTheme();
    }

    /// <summary>Respect Windows high contrast: use system colours instead of the mode's own.</summary>
    public static void ApplyTheme()
    {
        var res = Current.Resources;
        _retroFont ??= (FontFamily)res["TerminalFont"];
        res["TerminalFont"] = _plain ? new FontFamily("Consolas") : _retroFont;
        res["TextSize"] = _plain ? PlainTextSize : 20.0;
        res["SmallSize"] = _plain ? PlainTextSize : 18.0;
        res["TitleSize"] = _plain ? PlainTextSize : 24.0;
        res["HeadingSize"] = _plain ? PlainTextSize : 28.0;
        res["SizeColumnWidth"] = new GridLength(_plain ? 124 : 104);
        res["DateColumnWidth"] = new GridLength(_plain ? 124 : 96);

        if (SystemParameters.HighContrast)
        {
            res["Phosphor"] = SystemColors.WindowTextBrush;
            res["Dim"] = SystemColors.GrayTextBrush;
            res["ScreenBg"] = SystemColors.WindowBrush;
        }
        else if (_plain)
        {
            res["Phosphor"] = Frozen(Color.FromRgb(0xE4, 0xE4, 0xE4));
            res["Dim"] = Frozen(Color.FromRgb(0x96, 0x96, 0x96));
            res["ScreenBg"] = Frozen(Color.FromRgb(0x20, 0x20, 0x20));
        }
        else
        {
            res["Phosphor"] = Frozen(Color.FromRgb(0xFF, 0xB0, 0x00));
            res["Dim"] = Frozen(Color.FromRgb(0x7A, 0x54, 0x00));
            res["ScreenBg"] = Frozen(Color.FromRgb(0x0E, 0x0B, 0x07));
        }
    }

    private static SolidColorBrush Frozen(Color c)
    {
        var b = new SolidColorBrush(c);
        b.Freeze();
        return b;
    }

    private void OnUnhandled(object sender, DispatcherUnhandledExceptionEventArgs e)
    {
        SessionLog.Default().Write("UNHANDLED " + e.Exception);
        MessageBox.Show("WASM Chasm hit an unexpected error and will close. Details were written to the log in "
                        + SessionLog.DefaultDirectory + ".\n\n" + e.Exception.Message,
            "WASM Chasm", MessageBoxButton.OK, MessageBoxImage.Error);
        e.Handled = true;
        Shutdown(1);
    }
}
