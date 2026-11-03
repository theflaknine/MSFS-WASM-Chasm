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
        ApplyHighContrast();
        SystemParameters.StaticPropertyChanged += (_, args) =>
        {
            if (args.PropertyName == nameof(SystemParameters.HighContrast)) ApplyHighContrast();
        };

        new MainWindow().Show();
    }

    /// <summary>Respect Windows high contrast: use system colours instead of amber phosphor.</summary>
    public static void ApplyHighContrast()
    {
        var res = Current.Resources;
        if (SystemParameters.HighContrast)
        {
            res["Phosphor"] = SystemColors.WindowTextBrush;
            res["Dim"] = SystemColors.GrayTextBrush;
            res["ScreenBg"] = SystemColors.WindowBrush;
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
