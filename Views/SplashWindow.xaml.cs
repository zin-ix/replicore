using Wpf.Ui.Appearance;

namespace Replicore.Views;

public partial class SplashWindow : Wpf.Ui.Controls.FluentWindow
{
    public SplashWindow()
    {
        InitializeComponent();
        SystemThemeWatcher.Watch(this);
    }
}
