using System.Reflection;
using System.Windows;
using System.Windows.Media.Animation;
using Wpf.Ui.Appearance;

namespace Replicore.Views;

public partial class SplashWindow : Wpf.Ui.Controls.FluentWindow
{
    private static readonly Duration FadeDuration = new(TimeSpan.FromMilliseconds(200));

    public SplashWindow()
    {
        InitializeComponent();
        SystemThemeWatcher.Watch(this);

        Version? version = Assembly.GetExecutingAssembly().GetName().Version;
        VersionText.Text = version is null ? "" : $"Version {version.ToString(3)}";
    }

    /// <summary>Shows the current startup step under the progress bar.</summary>
    public void SetStatus(string message)
    {
        StatusText.Text = message;
        StatusText.BeginAnimation(OpacityProperty, new DoubleAnimation(0, 1, FadeDuration));
    }

    /// <summary>
    /// Fades the content out, then closes. The content fades rather than the window because
    /// Window.Opacity has no effect on a Mica window (it can't use AllowsTransparency).
    /// </summary>
    public Task CloseAnimatedAsync()
    {
        var closed = new TaskCompletionSource();
        var fade = new DoubleAnimation(0, FadeDuration);
        fade.Completed += (_, _) =>
        {
            Close();
            closed.SetResult();
        };
        Root.BeginAnimation(OpacityProperty, fade);
        return closed.Task;
    }
}
