using System.Windows;
using Wpf.Ui.Appearance;

namespace Replicore;

public partial class App : Application
{
    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);
        // Match the Windows light/dark setting before the first window is shown
        ApplicationThemeManager.ApplySystemTheme();
    }
}
