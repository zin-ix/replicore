using System.Windows;
using Replicore.Models;
using Replicore.Services;
using Replicore.Views;
using Wpf.Ui.Appearance;

namespace Replicore;

public partial class App : Application
{
    /// <summary>Keep the splash on screen at least this long so it doesn't just flicker.</summary>
    private static readonly TimeSpan MinimumSplashTime = TimeSpan.FromMilliseconds(1500);

    protected override async void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);
        // Match the Windows light/dark setting before the first window is shown
        ApplicationThemeManager.ApplySystemTheme();

        var splash = new SplashWindow();
        splash.Show();

        // Scan disks while the splash is visible; the main window picks up the result
        Task<List<DiskInfo>> initialScan = Task.Run(() => new DiskEnumerationService().GetPhysicalDisks());
        try
        {
            await Task.WhenAll(initialScan, Task.Delay(MinimumSplashTime));
        }
        catch
        {
            // A failed scan is reported by the main window, which offers "Try again"
        }

        var main = new MainWindow(initialScan);
        MainWindow = main;
        main.Show();
        splash.Close();
    }
}
