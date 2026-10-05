using Microsoft.Win32;
using System.Windows;
using System.Windows.Controls;
using TunnelGate.Core;
using TunnelGate.Models;

namespace TunnelGate.UI;

public partial class TunnelDialog : Window
{
    private readonly ProfileSettings _settings;
    private readonly TunnelDefinition _original;
    private bool _syncing;
    private bool _showPassword;
    public TunnelDefinition Result { get; private set; }

    public TunnelDialog(ProfileSettings settings, TunnelDefinition tunnel)
    {
        InitializeComponent();
        ThemeHelper.EnableDarkTitleBar(this);
        _settings = settings;
        _original = tunnel;
        Result = tunnel.Clone();
        NameBox.Text = tunnel.Name;
        ModeBox.SelectedIndex = tunnel.IsProxy ? 1 : 0;
        RemoteHostBox.Text = tunnel.RemoteHost;
        SshPortBox.Text = (tunnel.RemoteSshPort <= 0 ? 22 : tunnel.RemoteSshPort).ToString();
        UserBox.Text = tunnel.RemoteUser;
        PasswordBox.Password = tunnel.Password;
        PasswordRevealBox.Text = tunnel.Password;
        KeyFileBox.Text = tunnel.KeyFile;
        RemoteBindBox.Text = string.IsNullOrWhiteSpace(tunnel.RemoteBind) ? "127.0.0.1" : tunnel.RemoteBind;
        RemotePortBox.Text = tunnel.RemotePort == 0 ? "" : tunnel.RemotePort.ToString();
        LocalHostBox.Text = string.IsNullOrWhiteSpace(tunnel.LocalHost) ? "127.0.0.1" : tunnel.LocalHost;
        LocalPortBox.Text = tunnel.LocalPort == 0 ? "" : tunnel.LocalPort.ToString();
        EnabledBox.IsChecked = tunnel.Enabled;
        SavePasswordBox.IsChecked = tunnel.SavePassword;
        AutoRestartBox.IsChecked = tunnel.AutoRestart;
        DaysBox.Text = tunnel.AutoRestartDays.ToString();
        HoursBox.Text = tunnel.AutoRestartHours.ToString();
        MinutesBox.Text = tunnel.AutoRestartMinutes.ToString();
        Loaded += (_, _) => UpdateModeUi();
    }

    private string CurrentPassword => _showPassword ? PasswordRevealBox.Text : PasswordBox.Password;

    private void ModeBox_SelectionChanged(object sender, SelectionChangedEventArgs e) => UpdateModeUi();

    private void UpdateModeUi()
    {
        if (!IsLoaded) return;
        var proxy = ModeBox.SelectedIndex == 1;
        RemoteBindBox.IsEnabled = RemotePortBox.IsEnabled = !proxy;
        RemoteBindLabel.Opacity = RemotePortLabel.Opacity = proxy ? .45 : 1;
    }

    private void BrowseKey_Click(object sender, RoutedEventArgs e)
    {
        var dlg = new OpenFileDialog { Filter = "PuTTY private key (*.ppk)|*.ppk|All files (*.*)|*.*" };
        if (dlg.ShowDialog(this) == true) KeyFileBox.Text = dlg.FileName;
    }

    private void PasswordBox_PasswordChanged(object sender, RoutedEventArgs e)
    {
        if (_syncing) return;
        _syncing = true;
        PasswordRevealBox.Text = PasswordBox.Password;
        _syncing = false;
    }

    private void PasswordRevealBox_TextChanged(object sender, TextChangedEventArgs e)
    {
        if (_syncing) return;
        _syncing = true;
        PasswordBox.Password = PasswordRevealBox.Text;
        _syncing = false;
    }

    private void ShowPasswordButton_Click(object sender, RoutedEventArgs e)
    {
        _showPassword = !_showPassword;
        if (_showPassword)
        {
            PasswordRevealBox.Text = PasswordBox.Password;
            PasswordBox.Visibility = Visibility.Collapsed;
            PasswordRevealBox.Visibility = Visibility.Visible;
            ShowPasswordButton.Content = "Hide";
            PasswordRevealBox.Focus();
            PasswordRevealBox.CaretIndex = PasswordRevealBox.Text.Length;
        }
        else
        {
            PasswordBox.Password = PasswordRevealBox.Text;
            PasswordRevealBox.Visibility = Visibility.Collapsed;
            PasswordBox.Visibility = Visibility.Visible;
            ShowPasswordButton.Content = "Show";
            PasswordBox.Focus();
        }
    }

    private void Save_Click(object sender, RoutedEventArgs e)
    {
        var t = _original.Clone();
        t.Name = NameBox.Text.Trim();
        t.Mode = ModeBox.SelectedIndex == 1 ? "proxy" : "reverse";
        t.RemoteHost = RemoteHostBox.Text.Trim();
        t.RemoteSshPort = ParseInt(SshPortBox.Text, 22);
        t.RemoteUser = UserBox.Text.Trim();
        t.Password = CurrentPassword;
        t.SavePassword = SavePasswordBox.IsChecked == true;
        t.KeyFile = KeyFileBox.Text.Trim();
        t.RemoteBind = RemoteBindBox.Text.Trim();
        t.RemotePort = ParseInt(RemotePortBox.Text, 0);
        t.LocalHost = LocalHostBox.Text.Trim();
        t.LocalPort = ParseInt(LocalPortBox.Text, 0);
        t.Enabled = EnabledBox.IsChecked == true;
        t.AutoRestart = AutoRestartBox.IsChecked == true;
        t.AutoRestartDays = Math.Max(0, ParseInt(DaysBox.Text, 0));
        t.AutoRestartHours = Math.Max(0, ParseInt(HoursBox.Text, 0));
        t.AutoRestartMinutes = Math.Max(0, ParseInt(MinutesBox.Text, 0));
        if (t.AutoRestart && t.AutoRestartIntervalSeconds() < 60) t.AutoRestartMinutes = 1;
        var err = TunnelValidation.Validate(_settings, t);
        if (err is not null) { ErrorText.Text = err; return; }
        Result = t;
        DialogResult = true;
    }

    private static int ParseInt(string s, int fallback) => int.TryParse(s.Trim(), out var n) ? n : fallback;
    private void Cancel_Click(object sender, RoutedEventArgs e) => DialogResult = false;
}
