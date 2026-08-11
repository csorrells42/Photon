using System.Windows;

namespace HermesDesktop;

public partial class App : Application
{
    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);
        var options = DesktopOptions.Parse(e.Args);
        var window = new MainWindow(options);
        MainWindow = window;
        window.Show();
    }
}
