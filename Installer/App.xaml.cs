using System.Windows;

namespace TunnelGate.Setup;

public partial class App : Application
{
    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);

        if (e.Args.Any(a => string.Equals(a, "--uninstall", StringComparison.OrdinalIgnoreCase)))
        {
            InstallerEngine.Uninstall();
            Shutdown();
            return;
        }

        var main = new MainWindow();
        MainWindow = main;
        main.Show();
    }
}
