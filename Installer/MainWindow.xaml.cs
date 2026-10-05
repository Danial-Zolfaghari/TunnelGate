using Microsoft.Win32;
using System.IO;
using System.Windows;

namespace TunnelGate.Setup;

public partial class MainWindow : Window
{
    private bool _busy;
    private bool _completed;

    public MainWindow()
    {
        InitializeComponent();
        InstallPathBox.Text = InstallerEngine.GetSuggestedInstallDirectory();
        Loaded += (_, _) => InstallPathBox.CaretIndex = InstallPathBox.Text.Length;
    }

    private void Browse_Click(object sender, RoutedEventArgs e)
    {
        if (_busy) return;

        var current = InstallPathBox.Text.Trim();
        var initial = InstallerEngine.DefaultInstallDirectory;
        try
        {
            var normalized = InstallerEngine.NormalizeInstallDirectory(current);
            initial = Directory.Exists(normalized)
                ? normalized
                : Path.GetDirectoryName(normalized) ?? InstallerEngine.DefaultInstallDirectory;
        }
        catch { }

        var dialog = new OpenFolderDialog
        {
            Title = "Choose TunnelGate installation folder",
            InitialDirectory = initial,
            Multiselect = false
        };

        if (dialog.ShowDialog(this) == true)
        {
            InstallPathBox.Text = dialog.FolderName;
            InstallPathBox.CaretIndex = InstallPathBox.Text.Length;
        }
    }

    private async void Install_Click(object sender, RoutedEventArgs e)
    {
        if (_completed)
        {
            Close();
            return;
        }
        if (_busy) return;

        string installDirectory;
        try
        {
            installDirectory = InstallerEngine.NormalizeInstallDirectory(InstallPathBox.Text);
            InstallerEngine.ValidateInstallDirectory(installDirectory);
            InstallPathBox.Text = installDirectory;
        }
        catch (Exception ex)
        {
            StatusText.Text = ex.Message;
            StatusText.Foreground = (System.Windows.Media.Brush)FindResource("Danger");
            return;
        }

        SetBusy(true);
        try
        {
            SetProgress(18, "Preparing installation…");
            await Task.Delay(80);

            SetProgress(36, "Installing TunnelGate…");
            await Task.Run(() => InstallerEngine.Install(installDirectory));

            SetProgress(100, "TunnelGate installed successfully.");
            StatusText.Foreground = (System.Windows.Media.Brush)FindResource("Accent");
            _completed = true;
            _busy = false;
            InstallButton.IsEnabled = true;
            InstallButton.Content = "Done";
            BrowseButton.IsEnabled = false;
            InstallPathBox.IsEnabled = false;
            LaunchCheck.IsEnabled = false;

            if (LaunchCheck.IsChecked == true)
                InstallerEngine.LaunchInstalledApp(installDirectory);
        }
        catch (Exception ex)
        {
            SetProgress(0, $"Installation failed: {ex.Message}");
            StatusText.Foreground = (System.Windows.Media.Brush)FindResource("Danger");
            SetBusy(false);
        }
    }

    private void SetBusy(bool busy)
    {
        _busy = busy;
        InstallButton.IsEnabled = !busy;
        BrowseButton.IsEnabled = !busy;
        InstallPathBox.IsEnabled = !busy;
        LaunchCheck.IsEnabled = !busy;
    }

    private void SetProgress(double value, string status)
    {
        Progress.Value = value;
        ProgressText.Text = $"{(int)value}%";
        StatusText.Text = status;
        if (value < 100)
            StatusText.Foreground = (System.Windows.Media.Brush)FindResource("Muted");
    }

    private void Cancel_Click(object sender, RoutedEventArgs e)
    {
        if (!_busy) Close();
    }

    private void Close_Click(object sender, RoutedEventArgs e)
    {
        if (!_busy) Close();
    }
}
