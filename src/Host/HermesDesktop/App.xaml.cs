using System.Windows;

namespace HermesDesktop;

public partial class App : Application
{
    protected override void OnExit(ExitEventArgs e)
    {
        DesktopLog.Write($"Desktop application exit: code={e.ApplicationExitCode}.");
        base.OnExit(e);
    }

    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);
        var options = DesktopOptions.Parse(e.Args);
        var window = new MainWindow(options);
        MainWindow = window;
        window.SourceInitialized += (_, _) => DesktopLog.Write($"Desktop window source initialized: hwnd={new System.Windows.Interop.WindowInteropHelper(window).Handle}.");
        window.Loaded += (_, _) => DesktopLog.Write($"Desktop window loaded: hwnd={new System.Windows.Interop.WindowInteropHelper(window).Handle}.");
        window.Closed += (_, _) => DesktopLog.Write("Desktop window closed.");
        window.Show();
        // A desktop launched by Explorer can lose its foreground activation
        // while WebView2 starts.  Keep the operator-facing window present and
        // recoverable instead of leaving a healthy process with no usable UI.
        window.Activate();
        window.Dispatcher.BeginInvoke(() =>
        {
            if (!window.IsVisible) window.Show();
            if (window.WindowState == WindowState.Minimized) window.WindowState = WindowState.Normal;
            window.Activate();
            DesktopLog.Write($"Desktop window activation confirmed: visible={window.IsVisible}, state={window.WindowState}, hwnd={new System.Windows.Interop.WindowInteropHelper(window).Handle}.");
        }, System.Windows.Threading.DispatcherPriority.ApplicationIdle);
    }
}
