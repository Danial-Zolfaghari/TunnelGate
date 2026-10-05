using Microsoft.Win32;
using System.Diagnostics;
using System.Text;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Documents;
using System.Windows.Media;
using System.Windows.Shapes;
using TunnelGate.Core;
using TunnelGate.Models;

namespace TunnelGate.UI;

public partial class MainWindow : Window
{
    private readonly AppRuntime _runtime;
    private bool _treeBuilding;
    private bool _settingsLoading;
    private List<NetworkAdapterInfo> _adapters = [];
    private int _consoleLineCount;
    private System.Windows.Forms.NotifyIcon? _trayIcon;
    private System.Windows.Forms.ContextMenuStrip? _trayMenu;
    private System.Windows.Forms.ToolStripMenuItem? _trayStartItem;
    private System.Windows.Forms.ToolStripMenuItem? _trayRestartItem;
    private System.Windows.Forms.ToolStripMenuItem? _trayStopItem;
    private System.Drawing.Icon? _trayDrawingIcon;
    private bool _allowRealClose;

    public MainWindow(AppRuntime runtime)
    {
        InitializeComponent();
        ThemeHelper.EnableDarkTitleBar(this);
        _runtime = runtime;
        _runtime.LogReceived += Runtime_LogReceived;
        _runtime.Engine.StatusChanged += Engine_StatusChanged;
        _runtime.Engine.TunnelStateChanged += Engine_TunnelStateChanged;
        ConsoleBox.Document.PagePadding = new Thickness(0);
        InitializeTrayIcon();
        Loaded += MainWindow_Loaded;
        Closing += MainWindow_Closing;
        Closed += (_, _) =>
        {
            _runtime.LogReceived -= Runtime_LogReceived;
            _runtime.Engine.StatusChanged -= Engine_StatusChanged;
            _runtime.Engine.TunnelStateChanged -= Engine_TunnelStateChanged;
            DisposeTrayIcon();
        };
    }

    private void MainWindow_Loaded(object sender, RoutedEventArgs e)
    {
        RefreshProfileTree();
        LoadActiveProfile();
        RefreshNetwork();
        try
        {
            var startupEnabled = StartupManager.IsEnabled();
            if (_runtime.Vault.Current is not null && _runtime.Vault.Current.LaunchAtStartup != startupEnabled)
            {
                _runtime.Vault.Current.LaunchAtStartup = startupEnabled;
                _runtime.Vault.Save();
                LaunchStartupBox.IsChecked = startupEnabled;
            }
        }
        catch (Exception ex) { _runtime.Log($"[STARTUP] {ex.Message}"); }
        // Auto-start is initiated by App before/while the vault gate is shown.
        // Do not run a duplicate startup pass when MainWindow appears.
        AutoStartTunnelsBox.IsChecked = true;
        RefreshTunnels();
        var startupStatus = _runtime.Engine.AnyLive
            ? EngineStatus.Connected
            : _runtime.Engine.AnyRunning ? EngineStatus.Connecting : EngineStatus.Idle;
        Engine_StatusChanged(startupStatus);
    }

    private VaultData Vault => _runtime.Vault.Current ?? throw new InvalidOperationException("Vault unavailable.");
    private VaultProfile ActiveProfile => _runtime.Vault.ActiveProfile();

    private void Runtime_LogReceived(string line)
    {
        Dispatcher.InvokeAsync(() => AppendConsoleLine(line));
    }

    private void AppendConsoleLine(string line)
    {
        var paragraph = new Paragraph
        {
            Margin = new Thickness(0, 0, 0, 2),
            LineHeight = 18
        };

        var remaining = line;
        if (remaining.StartsWith("[", StringComparison.Ordinal))
        {
            var end = remaining.IndexOf(']');
            if (end > 0)
            {
                var timestamp = remaining[..(end + 1)];
                paragraph.Inlines.Add(new Run(timestamp + " ")
                {
                    Foreground = ConsoleBrush("timestamp"),
                    FontWeight = FontWeights.SemiBold
                });
                remaining = remaining[(end + 1)..].TrimStart();
            }
        }

        if (remaining.StartsWith("[", StringComparison.Ordinal))
        {
            var end = remaining.IndexOf(']');
            if (end > 0)
            {
                var tag = remaining[..(end + 1)];
                paragraph.Inlines.Add(new Run(tag + " ")
                {
                    Foreground = ConsoleBrush(tag),
                    FontWeight = FontWeights.Bold
                });
                remaining = remaining[(end + 1)..].TrimStart();
            }
        }

        paragraph.Inlines.Add(new Run(remaining) { Foreground = ConsoleBrush("text") });
        ConsoleBox.Document.Blocks.Add(paragraph);
        _consoleLineCount++;

        while (ConsoleBox.Document.Blocks.Count > 5000)
        {
            ConsoleBox.Document.Blocks.Remove(ConsoleBox.Document.Blocks.FirstBlock);
            _consoleLineCount = Math.Max(0, _consoleLineCount - 1);
        }

        ConsoleLineCountText.Text = $"{_consoleLineCount:N0} lines";
        ConsoleBox.ScrollToEnd();
    }

    private Brush ConsoleBrush(string tag)
    {
        tag = tag.ToUpperInvariant();
        if (tag.Contains("ERROR") || tag.Contains("FAIL")) return (Brush)FindResource("Danger");
        if (tag.Contains("LIVE") || tag.Contains("READY") || tag.Contains("SUCCESS")) return (Brush)FindResource("Accent");
        if (tag.Contains("VERIFY")) return BrushFrom("#58C8FF");
        if (tag.Contains("PLINK") || tag.Contains("SSH")) return BrushFrom("#B39DFF");
        if (tag.Contains("CONNECT") || tag.Contains("RECONNECT")) return (Brush)FindResource("Warning");
        if (tag.Contains("DNS") || tag.Contains("NETWORK")) return BrushFrom("#65D6D2");
        if (tag == "TIMESTAMP") return BrushFrom("#687D99");
        return BrushFrom("#CCD7E5");
    }

    private static SolidColorBrush BrushFrom(string hex)
    {
        var brush = new SolidColorBrush((Color)ColorConverter.ConvertFromString(hex));
        brush.Freeze();
        return brush;
    }

    private void Engine_StatusChanged(EngineStatus status)
    {
        Dispatcher.InvokeAsync(() =>
        {
            StatusText.Text = status.ToString();
            StatusText.Foreground = status == EngineStatus.Connected ? (System.Windows.Media.Brush)FindResource("Accent") : (System.Windows.Media.Brush)FindResource("Muted");
            UpdateTrayState(status);
        });
    }

    private void Engine_TunnelStateChanged(string uid, bool live) => Dispatcher.InvokeAsync(RefreshTunnels);

    private void RefreshProfileTree()
    {
        _treeBuilding = true;
        try
        {
            ProfileTree.Items.Clear();
            TreeViewItem? activeItem = null;
            var categories = Vault.Categories.OrderBy(c => c.SortOrder).ToList();

            foreach (var category in categories)
            {
                var profiles = Vault.Profiles.Where(p => p.CategoryUid == category.Uid).ToList();
                var ci = new TreeViewItem
                {
                    Header = BuildCategoryHeader(category, profiles.Count),
                    Tag = category,
                    IsExpanded = true,
                    Foreground = (Brush)FindResource(category.Enabled ? "Text" : "Muted"),
                    ContextMenu = BuildCategoryContextMenu(category)
                };

                foreach (var profile in profiles)
                {
                    var pi = new TreeViewItem
                    {
                        Header = BuildProfileHeader(profile, category),
                        Tag = profile,
                        Foreground = (Brush)FindResource(profile.Enabled && category.Enabled ? "Text" : "Muted"),
                        ContextMenu = BuildProfileContextMenu(profile)
                    };
                    if (profile.Uid == Vault.ActiveProfileUid) activeItem = pi;
                    ci.Items.Add(pi);
                }
                ProfileTree.Items.Add(ci);
            }

            ProfileSummaryText.Text = $"{Vault.Categories.Count} categories  •  {Vault.Profiles.Count} profiles";
            if (activeItem is not null) activeItem.IsSelected = true;
        }
        finally { _treeBuilding = false; }
    }

    private FrameworkElement BuildCategoryHeader(VaultCategory category, int profileCount)
    {
        var grid = new Grid { HorizontalAlignment = HorizontalAlignment.Stretch };
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

        var icon = new Border
        {
            Width = 28,
            Height = 28,
            CornerRadius = new CornerRadius(8),
            Background = category.Enabled ? BrushFrom("#153B2B") : BrushFrom("#1A2230"),
            Margin = new Thickness(0, 0, 9, 0),
            Child = new TextBlock
            {
                Text = "C",
                FontWeight = FontWeights.Bold,
                Foreground = category.Enabled ? (Brush)FindResource("Accent") : (Brush)FindResource("Muted"),
                HorizontalAlignment = HorizontalAlignment.Center,
                VerticalAlignment = VerticalAlignment.Center
            }
        };
        Grid.SetColumn(icon, 0);
        grid.Children.Add(icon);

        var text = new TextBlock
        {
            Text = category.Name,
            FontWeight = FontWeights.SemiBold,
            VerticalAlignment = VerticalAlignment.Center,
            TextTrimming = TextTrimming.CharacterEllipsis
        };
        Grid.SetColumn(text, 1);
        grid.Children.Add(text);

        var count = new Border
        {
            Padding = new Thickness(7, 2, 7, 2),
            CornerRadius = new CornerRadius(999),
            Background = BrushFrom("#101B2A"),
            BorderBrush = (Brush)FindResource("Border"),
            BorderThickness = new Thickness(1),
            Child = new TextBlock
            {
                Text = profileCount.ToString(),
                FontSize = 9.5,
                Foreground = (Brush)FindResource("Muted")
            }
        };
        Grid.SetColumn(count, 2);
        grid.Children.Add(count);
        return grid;
    }

    private FrameworkElement BuildProfileHeader(VaultProfile profile, VaultCategory category)
    {
        var grid = new Grid { HorizontalAlignment = HorizontalAlignment.Stretch };
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

        var enabled = profile.Enabled && category.Enabled;
        var dot = new Ellipse
        {
            Width = 7,
            Height = 7,
            Fill = enabled ? (Brush)FindResource("Accent") : (Brush)FindResource("Muted2"),
            Margin = new Thickness(2, 0, 10, 0),
            VerticalAlignment = VerticalAlignment.Center
        };
        Grid.SetColumn(dot, 0);
        grid.Children.Add(dot);

        var name = new TextBlock
        {
            Text = profile.Name,
            VerticalAlignment = VerticalAlignment.Center,
            TextTrimming = TextTrimming.CharacterEllipsis,
            Foreground = enabled ? (Brush)FindResource("Text") : (Brush)FindResource("Muted")
        };
        Grid.SetColumn(name, 1);
        grid.Children.Add(name);

        if (profile.Uid == Vault.ActiveProfileUid)
        {
            var badge = new Border
            {
                Padding = new Thickness(7, 2, 7, 2),
                CornerRadius = new CornerRadius(999),
                Background = BrushFrom("#143829"),
                Child = new TextBlock
                {
                    Text = "ACTIVE",
                    FontSize = 8.5,
                    FontWeight = FontWeights.Bold,
                    Foreground = (Brush)FindResource("Accent")
                }
            };
            Grid.SetColumn(badge, 2);
            grid.Children.Add(badge);
        }
        return grid;
    }

    private ContextMenu BuildCategoryContextMenu(VaultCategory category)
    {
        var menu = new ContextMenu();
        menu.Items.Add(ContextItem("Add profile", (_, _) => AddProfileToCategory(category)));
        menu.Items.Add(ContextItem("Edit category", (_, _) => EditCategory(category)));
        menu.Items.Add(ContextItem("Delete category", (_, _) => DeleteSelected_Click(this, new RoutedEventArgs())));
        return menu;
    }

    private ContextMenu BuildProfileContextMenu(VaultProfile profile)
    {
        var menu = new ContextMenu();
        menu.Items.Add(ContextItem("Edit profile", (_, _) => EditProfile(profile)));
        menu.Items.Add(ContextItem("Delete profile", (_, _) => DeleteSelected_Click(this, new RoutedEventArgs())));
        return menu;
    }

    private static MenuItem ContextItem(string header, RoutedEventHandler handler)
    {
        var item = new MenuItem { Header = header };
        item.Click += handler;
        return item;
    }

    private void ProfileTree_PreviewMouseRightButtonDown(object sender, MouseButtonEventArgs e)
    {
        var item = FindVisualParent<TreeViewItem>(e.OriginalSource as DependencyObject);
        if (item is null) return;
        item.IsSelected = true;
        item.Focus();
    }

    private static T? FindVisualParent<T>(DependencyObject? child) where T : DependencyObject
    {
        while (child is not null)
        {
            if (child is T typed) return typed;
            child = VisualTreeHelper.GetParent(child);
        }
        return null;
    }

    private void ContextNewCategory_Click(object sender, RoutedEventArgs e) => AddCategory_Click(sender, e);

    private void EditCategory(VaultCategory category)
    {
        var dlg = new NameDialog("Edit category", "Category name", category.Name) { Owner = this };
        if (dlg.ShowDialog() != true) return;
        category.Name = dlg.Value;
        _runtime.Vault.Save();
        RefreshProfileTree();
    }

    private void EditProfile(VaultProfile profile)
    {
        var dlg = new NameDialog("Edit profile", "Profile name", profile.Name) { Owner = this };
        if (dlg.ShowDialog() != true) return;
        profile.Name = dlg.Value;
        _runtime.Vault.Save();
        RefreshProfileTree();
        LoadActiveProfile();
    }

    private void AddProfileToCategory(VaultCategory category)
    {
        var dlg = new NameDialog("New profile", $"Profile name in {category.Name}") { Owner = this };
        if (dlg.ShowDialog() != true) return;
        try
        {
            _runtime.Vault.AddProfile(category.Uid, dlg.Value);
            RefreshProfileTree();
            LoadActiveProfile();
        }
        catch (Exception ex) { ShowError(ex); }
    }

    private void LoadActiveProfile()
    {
        var profile = ActiveProfile;
        ActiveProfileText.Text = $"Profile: {profile.Name}";
        _settingsLoading = true;
        ReconnectBox.IsChecked = profile.Settings.Reconnect;
        ReconnectDelayBox.Text = profile.Settings.ReconnectDelay.ToString();
        KeepAliveBox.Text = profile.Settings.KeepAlive.ToString();
        CompressionBox.IsChecked = profile.Settings.Compression;
        AutoHostKeyBox.IsChecked = profile.Settings.AutoStoreHostKey;
        AutoStartTunnelsBox.IsChecked = Vault.AutoStartTunnels;
        LaunchStartupBox.IsChecked = Vault.LaunchAtStartup;
        _settingsLoading = false;
        RefreshTunnels();
    }

    private void RefreshTunnels()
    {
        if (!_runtime.Vault.IsUnlocked) return;
        var live = _runtime.Engine.LiveStates;
        var rows = ActiveProfile.Settings.Tunnels.Select(t =>
        {
            var isLive = live.TryGetValue(t.Uid, out var liveState) && liveState;
            return new TunnelGridRow
            {
                Uid = t.Uid,
                IsLive = isLive,
                Live = isLive ? "LIVE" : "STOPPED",
                IsEnabled = t.Enabled,
                Enabled = t.Enabled ? "ENABLED" : "DISABLED",
                Name = t.DisplayName,
                Mode = t.IsProxy ? "SOCKS" : "REVERSE",
                Route = t.Route,
                Ssh = $"{t.RemoteUser}@{t.RemoteHost}:{(t.RemoteSshPort <= 0 ? 22 : t.RemoteSshPort)}"
            };
        }).ToList();
        TunnelGrid.ItemsSource = rows;
        TunnelCountText.Text = rows.Count == 1 ? "1 tunnel" : $"{rows.Count} tunnels";
    }

    private void PersistSettingsFromUi()
    {
        if (_settingsLoading || !_runtime.Vault.IsUnlocked) return;
        var settings = ActiveProfile.Settings;
        settings.Reconnect = ReconnectBox.IsChecked == true;
        settings.ReconnectDelay = ParsePositive(ReconnectDelayBox.Text, 5);
        settings.KeepAlive = Math.Max(0, ParseInt(KeepAliveBox.Text, 30));
        settings.Compression = CompressionBox.IsChecked == true;
        settings.AutoStoreHostKey = AutoHostKeyBox.IsChecked == true;
        Vault.AutoStartTunnels = true;
        Vault.LaunchAtStartup = LaunchStartupBox.IsChecked == true;
        _runtime.Vault.Save();
    }

    private async void Start_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            PersistSettingsFromUi();
            var category = Vault.Categories.FirstOrDefault(c => c.Uid == ActiveProfile.CategoryUid);
            if (!ActiveProfile.Enabled || category is { Enabled: false }) throw new InvalidOperationException("Active profile/category is disabled.");
            await _runtime.Engine.StartProfileAsync(ActiveProfile);
        }
        catch (Exception ex) { ShowError(ex); }
    }

    private async void Stop_Click(object sender, RoutedEventArgs e)
    {
        try { await _runtime.Engine.StopAllAsync(); RefreshTunnels(); }
        catch (Exception ex) { ShowError(ex); }
    }

    private async void Restart_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            PersistSettingsFromUi();
            await _runtime.Engine.StopAllAsync();
            await _runtime.Engine.StartProfileAsync(ActiveProfile);
        }
        catch (Exception ex) { ShowError(ex); }
    }

    private void AddCategory_Click(object sender, RoutedEventArgs e)
    {
        var dlg = new NameDialog("New category", "Category name") { Owner = this };
        if (dlg.ShowDialog() == true)
        {
            try { _runtime.Vault.AddCategory(dlg.Value); RefreshProfileTree(); }
            catch (Exception ex) { ShowError(ex); }
        }
    }

    private void AddProfile_Click(object sender, RoutedEventArgs e)
    {
        var category = SelectedTag() as VaultCategory
                       ?? Vault.Categories.FirstOrDefault(c => c.Uid == ActiveProfile.CategoryUid)
                       ?? Vault.Categories.First();
        AddProfileToCategory(category);
    }

    private void RenameSelected_Click(object sender, RoutedEventArgs e)
    {
        var tag = SelectedTag();
        if (tag is VaultCategory c) EditCategory(c);
        else if (tag is VaultProfile p) EditProfile(p);
    }

    private void ToggleSelected_Click(object sender, RoutedEventArgs e)
    {
        var tag = SelectedTag();
        if (tag is VaultCategory c) c.Enabled = !c.Enabled;
        else if (tag is VaultProfile p) p.Enabled = !p.Enabled;
        else return;
        _runtime.Vault.Save(); RefreshProfileTree();
    }

    private async void DeleteSelected_Click(object sender, RoutedEventArgs e)
    {
        var tag = SelectedTag();
        if (tag is VaultProfile p)
        {
            var profileConfirm = new ConfirmDialog("Delete profile", $"Delete profile '{p.Name}'?", "Delete") { Owner = this };
            if (profileConfirm.ShowDialog() != true) return;
            foreach (var t in p.Settings.Tunnels) await _runtime.Engine.StopTunnelAsync(t.Uid);
            _runtime.Vault.DeleteProfile(p.Uid);
        }
        else if (tag is VaultCategory c)
        {
            var profiles = Vault.Profiles.Where(p => p.CategoryUid == c.Uid).ToList();
            var categoryConfirm = new ConfirmDialog("Delete category", $"Delete category '{c.Name}' and {profiles.Count} profile(s)?", "Delete") { Owner = this };
            if (categoryConfirm.ShowDialog() != true) return;
            foreach (var t in profiles.SelectMany(p => p.Settings.Tunnels)) await _runtime.Engine.StopTunnelAsync(t.Uid);
            _runtime.Vault.DeleteCategory(c.Uid);
        }
        else return;
        RefreshProfileTree(); LoadActiveProfile();
    }

    private void ProfileTree_SelectedItemChanged(object sender, RoutedPropertyChangedEventArgs<object> e)
    {
        if (_treeBuilding) return;
        if ((e.NewValue as TreeViewItem)?.Tag is not VaultProfile profile) return;
        try
        {
            PersistSettingsFromUi();
            _runtime.Vault.SetActiveProfile(profile.Uid);
            LoadActiveProfile();
        }
        catch (Exception ex) { ShowError(ex); }
    }

    private object? SelectedTag() => (ProfileTree.SelectedItem as TreeViewItem)?.Tag;

    private async void AddTunnel_Click(object sender, RoutedEventArgs e)
    {
        PersistSettingsFromUi();
        var last = ActiveProfile.Settings.Tunnels.LastOrDefault();
        var tunnel = new TunnelDefinition
        {
            RemoteSshPort = last?.RemoteSshPort ?? 22,
            RemoteHost = last?.RemoteHost ?? "",
            RemoteUser = last?.RemoteUser ?? "",
            RemoteBind = "127.0.0.1",
            LocalHost = "127.0.0.1",
            LocalPort = 0,
            RemotePort = 0
        };
        var dlg = new TunnelDialog(ActiveProfile.Settings, tunnel) { Owner = this };
        if (dlg.ShowDialog() != true) return;
        ActiveProfile.Settings.Tunnels.Add(dlg.Result);
        _runtime.Vault.Save(); RefreshTunnels();
        if (_runtime.Engine.AnyRunning && dlg.Result.Enabled)
        {
            try { await _runtime.Engine.RestartTunnelAsync(ActiveProfile.Settings, dlg.Result); }
            catch (Exception ex) { ShowError(ex); }
        }
    }

    private async void EditTunnel_Click(object sender, RoutedEventArgs e)
    {
        var tunnel = SelectedTunnel(); if (tunnel is null) return;
        PersistSettingsFromUi();
        var dlg = new TunnelDialog(ActiveProfile.Settings, tunnel) { Owner = this };
        if (dlg.ShowDialog() != true) return;
        var idx = ActiveProfile.Settings.Tunnels.FindIndex(t => t.Uid == tunnel.Uid);
        if (idx >= 0) ActiveProfile.Settings.Tunnels[idx] = dlg.Result;
        _runtime.Vault.Save(); RefreshTunnels();
        if (_runtime.Engine.LiveStates.ContainsKey(tunnel.Uid))
        {
            try
            {
                if (dlg.Result.Enabled) await _runtime.Engine.RestartTunnelAsync(ActiveProfile.Settings, dlg.Result);
                else await _runtime.Engine.StopTunnelAsync(tunnel.Uid);
            }
            catch (Exception ex) { ShowError(ex); }
        }
    }

    private void TunnelGrid_MouseDoubleClick(object sender, MouseButtonEventArgs e) => EditTunnel_Click(sender, e);

    private async void DeleteTunnel_Click(object sender, RoutedEventArgs e)
    {
        var tunnel = SelectedTunnel(); if (tunnel is null) return;
        if (MessageBox.Show(this, $"Delete tunnel '{tunnel.DisplayName}'?", "Confirm", MessageBoxButton.YesNo, MessageBoxImage.Warning) != MessageBoxResult.Yes) return;
        await _runtime.Engine.StopTunnelAsync(tunnel.Uid);
        ActiveProfile.Settings.Tunnels.RemoveAll(t => t.Uid == tunnel.Uid);
        _runtime.Vault.Save(); RefreshTunnels();
    }

    private async void ToggleTunnel_Click(object sender, RoutedEventArgs e)
    {
        var tunnel = SelectedTunnel(); if (tunnel is null) return;
        tunnel.Enabled = !tunnel.Enabled;
        _runtime.Vault.Save();
        try
        {
            if (!tunnel.Enabled) await _runtime.Engine.StopTunnelAsync(tunnel.Uid);
            else if (_runtime.Engine.AnyRunning) await _runtime.Engine.RestartTunnelAsync(ActiveProfile.Settings, tunnel);
        }
        catch (Exception ex) { ShowError(ex); }
        RefreshTunnels();
    }

    private async void RestartTunnel_Click(object sender, RoutedEventArgs e)
    {
        var tunnel = SelectedTunnel(); if (tunnel is null || !tunnel.Enabled) return;
        try { PersistSettingsFromUi(); await _runtime.Engine.RestartTunnelAsync(ActiveProfile.Settings, tunnel); }
        catch (Exception ex) { ShowError(ex); }
    }

    private TunnelDefinition? SelectedTunnel()
    {
        if (TunnelGrid.SelectedItem is not TunnelGridRow row) return null;
        return ActiveProfile.Settings.Tunnels.FirstOrDefault(t => t.Uid == row.Uid);
    }

    private void SaveSettings_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            PersistSettingsFromUi();
            StartupManager.SetEnabled(Vault.LaunchAtStartup);
            _runtime.Log("[SETTINGS] Saved.");
        }
        catch (Exception ex) { ShowError(ex); }
    }

    private void RefreshNetwork_Click(object sender, RoutedEventArgs e) => RefreshNetwork();

    private void RefreshNetwork()
    {
        try
        {
            _adapters = NetworkTools.ListAdapters();
            AdapterBox.ItemsSource = _adapters;
            if (_adapters.Count > 0 && AdapterBox.SelectedIndex < 0) AdapterBox.SelectedIndex = 0;
            var sb = new StringBuilder();
            foreach (var a in _adapters)
            {
                sb.AppendLine(a.Name);
                sb.AppendLine($"  Status: {a.Status}");
                sb.AppendLine($"  MAC: {a.Mac}");
                sb.AppendLine($"  IPv4: {(a.IpAddresses.Count == 0 ? "—" : string.Join(", ", a.IpAddresses))}");
                sb.AppendLine($"  DNS: {(a.DnsServers.Count == 0 ? "DHCP / none" : string.Join(", ", a.DnsServers))}");
                sb.AppendLine();
            }
            NetworkInfoBox.Text = sb.ToString();
        }
        catch (Exception ex) { ShowError(ex); }
    }

    private void AdapterBox_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (AdapterBox.SelectedItem is not NetworkAdapterInfo a) return;
        PrimaryDnsBox.Text = a.DnsServers.ElementAtOrDefault(0) ?? "";
        SecondaryDnsBox.Text = a.DnsServers.ElementAtOrDefault(1) ?? "";
    }

    private async void ApplyDns_Click(object sender, RoutedEventArgs e)
    {
        if (AdapterBox.SelectedItem is not NetworkAdapterInfo a) return;
        try { await NetworkTools.SetDnsAsync(a.Name, PrimaryDnsBox.Text.Trim(), SecondaryDnsBox.Text.Trim()); _runtime.Log($"[DNS] Updated {a.Name}."); RefreshNetwork(); }
        catch (Exception ex) { ShowError(new InvalidOperationException(ex.Message + "\nIf access is denied, run TunnelGate as Administrator for DNS changes.")); }
    }

    private async void ResetDns_Click(object sender, RoutedEventArgs e)
    {
        if (AdapterBox.SelectedItem is not NetworkAdapterInfo a) return;
        try { await NetworkTools.ResetDnsAsync(a.Name); _runtime.Log($"[DNS] Reset {a.Name} to DHCP."); RefreshNetwork(); }
        catch (Exception ex) { ShowError(new InvalidOperationException(ex.Message + "\nIf access is denied, run TunnelGate as Administrator for DNS changes.")); }
    }

    private void ChangePassword_Click(object sender, RoutedEventArgs e)
    {
        var dlg = new ChangePasswordDialog(_runtime.Vault) { Owner = this };
        if (dlg.ShowDialog() == true) MessageBox.Show(this, "Vault password changed.", "TunnelGate", MessageBoxButton.OK, MessageBoxImage.Information);
    }

    private void ExportBackup_Click(object sender, RoutedEventArgs e)
    {
        var pw = new PasswordPromptDialog("Export backup", "Choose a separate password for this backup file.", true) { Owner = this };
        if (pw.ShowDialog() != true) return;
        try
        {
            PersistSettingsFromUi();
            var content = _runtime.Vault.ExportBackup(pw.Password);
            var dlg = new SaveFileDialog { Filter = "TunnelGate backup (*.tgbak)|*.tgbak", FileName = $"TunnelGate-backup-{DateTime.Now:yyyyMMdd-HHmm}.tgbak" };
            if (dlg.ShowDialog(this) == true) File.WriteAllText(dlg.FileName, content, Encoding.UTF8);
        }
        catch (Exception ex) { ShowError(ex); }
    }

    private async void ImportBackup_Click(object sender, RoutedEventArgs e)
    {
        var open = new OpenFileDialog { Filter = "TunnelGate backup (*.tgbak)|*.tgbak|All files (*.*)|*.*" };
        if (open.ShowDialog(this) != true) return;
        var pw = new PasswordPromptDialog("Import backup", "Enter the backup-file password.", false) { Owner = this };
        if (pw.ShowDialog() != true) return;
        try
        {
            var text = File.ReadAllText(open.FileName, Encoding.UTF8);
            var preview = _runtime.Vault.PreviewBackup(text, pw.Password);
            var choice = MessageBox.Show(this,
                $"Backup {preview.ExportedAt.ToLocalTime():yyyy-MM-dd HH:mm}\n{preview.Categories} categories · {preview.Profiles} profiles · {preview.Tunnels} tunnels\n\nYes = Replace current vault\nNo = Merge into current vault\nCancel = Abort",
                "Import backup", MessageBoxButton.YesNoCancel, MessageBoxImage.Question);
            if (choice == MessageBoxResult.Cancel) return;
            await _runtime.Engine.StopAllAsync();
            _runtime.Vault.ImportBackup(text, pw.Password, choice == MessageBoxResult.Yes);
            RefreshProfileTree(); LoadActiveProfile();
        }
        catch (Exception ex) { ShowError(ex); }
    }

    private async void ImportLegacy_Click(object sender, RoutedEventArgs e)
    {
        var open = new OpenFileDialog { Filter = "TunnelGate migration JSON (*.tglegacy.json)|*.tglegacy.json|JSON (*.json)|*.json|All files (*.*)|*.*" };
        if (open.ShowDialog(this) != true) return;
        try
        {
            var choice = MessageBox.Show(this,
                "Legacy migration files are plaintext and can contain saved SSH passwords.\n\nYes = Replace current vault\nNo = Merge into current vault\nCancel = Abort\n\nDelete the migration file after import.",
                "Import legacy TunnelGate", MessageBoxButton.YesNoCancel, MessageBoxImage.Warning);
            if (choice == MessageBoxResult.Cancel) return;
            await _runtime.Engine.StopAllAsync();
            _runtime.Vault.ImportLegacyJson(File.ReadAllText(open.FileName, Encoding.UTF8), choice == MessageBoxResult.Yes);
            RefreshProfileTree(); LoadActiveProfile();
            MessageBox.Show(this, "Legacy data imported. Delete the plaintext migration JSON now.", "TunnelGate", MessageBoxButton.OK, MessageBoxImage.Information);
        }
        catch (Exception ex) { ShowError(ex); }
    }

    private void ClearConsole_Click(object sender, RoutedEventArgs e)
    {
        ConsoleBox.Document.Blocks.Clear();
        _consoleLineCount = 0;
        ConsoleLineCountText.Text = "0 lines";
    }
    private void OpenLogFolder_Click(object sender, RoutedEventArgs e)
    {
        try { Process.Start(new ProcessStartInfo("explorer.exe", AppPaths.DataDir) { UseShellExecute = true }); } catch { }
    }

    private void InitializeTrayIcon()
    {
        try
        {
            var exePath = Environment.ProcessPath;
            if (!string.IsNullOrWhiteSpace(exePath))
                _trayDrawingIcon = System.Drawing.Icon.ExtractAssociatedIcon(exePath);
        }
        catch { }

        _trayDrawingIcon ??= (System.Drawing.Icon)System.Drawing.SystemIcons.Application.Clone();

        _trayMenu = new System.Windows.Forms.ContextMenuStrip
        {
            ShowImageMargin = false,
            BackColor = System.Drawing.Color.FromArgb(12, 20, 31),
            ForeColor = System.Drawing.Color.FromArgb(242, 246, 251),
            Padding = new System.Windows.Forms.Padding(6),
            Renderer = new System.Windows.Forms.ToolStripProfessionalRenderer(new TrayColorTable())
        };

        var openItem = new System.Windows.Forms.ToolStripMenuItem("Open TunnelGate");
        openItem.Font = new System.Drawing.Font(openItem.Font, System.Drawing.FontStyle.Bold);
        openItem.Click += (_, _) => Dispatcher.Invoke(RestoreFromTray);

        _trayStartItem = new System.Windows.Forms.ToolStripMenuItem("Start");
        _trayStartItem.Click += async (_, _) => await TrayStartAsync();

        _trayRestartItem = new System.Windows.Forms.ToolStripMenuItem("Restart");
        _trayRestartItem.Click += async (_, _) => await TrayRestartAsync();

        _trayStopItem = new System.Windows.Forms.ToolStripMenuItem("Stop");
        _trayStopItem.Click += async (_, _) => await TrayStopAsync();

        var quitItem = new System.Windows.Forms.ToolStripMenuItem("Quit");
        quitItem.ForeColor = System.Drawing.Color.FromArgb(255, 124, 139);
        quitItem.Click += async (_, _) => await QuitFromTrayAsync();

        _trayMenu.Items.Add(openItem);
        _trayMenu.Items.Add(new System.Windows.Forms.ToolStripSeparator());
        _trayMenu.Items.Add(_trayStartItem);
        _trayMenu.Items.Add(_trayRestartItem);
        _trayMenu.Items.Add(_trayStopItem);
        _trayMenu.Items.Add(new System.Windows.Forms.ToolStripSeparator());
        _trayMenu.Items.Add(quitItem);
        _trayMenu.Opening += (_, _) => UpdateTrayMenuVisibility();

        _trayIcon = new System.Windows.Forms.NotifyIcon
        {
            Icon = _trayDrawingIcon,
            Text = "TunnelGate - Idle",
            ContextMenuStrip = _trayMenu,
            Visible = true
        };
        _trayIcon.DoubleClick += (_, _) => Dispatcher.Invoke(RestoreFromTray);
        _trayIcon.MouseClick += (_, e) =>
        {
            if (e.Button == System.Windows.Forms.MouseButtons.Left)
                Dispatcher.Invoke(RestoreFromTray);
        };
        UpdateTrayMenuVisibility();
    }

    public void AllowApplicationExit()
    {
        _allowRealClose = true;
        if (_trayIcon is not null) _trayIcon.Visible = false;
    }

    private void MainWindow_Closing(object? sender, System.ComponentModel.CancelEventArgs e)
    {
        if (_allowRealClose) return;
        e.Cancel = true;
        HideToTray();
    }

    private void HideToTray()
    {
        ShowInTaskbar = false;
        Hide();
        if (_trayIcon is not null)
        {
            _trayIcon.Visible = true;
            _trayIcon.Text = _runtime.Engine.AnyLive
                ? "TunnelGate - Connected"
                : _runtime.Engine.AnyRunning ? "TunnelGate - Connecting" : "TunnelGate - Idle";
        }
    }

    private void RestoreFromTray()
    {
        if (!IsVisible)
            Show();
        ShowInTaskbar = true;
        if (WindowState == WindowState.Minimized)
            WindowState = WindowState.Normal;
        Activate();
        Topmost = true;
        Topmost = false;
        Focus();
    }

    private void UpdateTrayMenuVisibility()
    {
        if (_trayStartItem is null || _trayRestartItem is null || _trayStopItem is null) return;
        var running = _runtime.Engine.AnyRunning;
        _trayStartItem.Visible = true;
        _trayStartItem.Enabled = !running;
        _trayRestartItem.Visible = running;
        _trayStopItem.Visible = running;
    }

    private void UpdateTrayState(EngineStatus status)
    {
        if (_trayIcon is null) return;
        _trayIcon.Text = status switch
        {
            EngineStatus.Connected => "TunnelGate - Connected",
            EngineStatus.Connecting => "TunnelGate - Connecting",
            EngineStatus.Stopped => "TunnelGate - Stopped",
            _ => "TunnelGate - Idle"
        };
        UpdateTrayMenuVisibility();
    }

    private async Task TrayStartAsync()
    {
        try
        {
            await Dispatcher.InvokeAsync(PersistSettingsFromUi);
            await _runtime.AutoStartAsync();
            await Dispatcher.InvokeAsync(RefreshTunnels);
        }
        catch (Exception ex)
        {
            _runtime.Log($"[TRAY] Start failed: {ex.Message}");
        }
    }

    private async Task TrayRestartAsync()
    {
        try
        {
            await Dispatcher.InvokeAsync(PersistSettingsFromUi);
            await _runtime.Engine.StopAllAsync();
            await _runtime.AutoStartAsync();
            await Dispatcher.InvokeAsync(RefreshTunnels);
        }
        catch (Exception ex)
        {
            _runtime.Log($"[TRAY] Restart failed: {ex.Message}");
        }
    }

    private async Task TrayStopAsync()
    {
        try
        {
            await _runtime.Engine.StopAllAsync();
            await Dispatcher.InvokeAsync(RefreshTunnels);
        }
        catch (Exception ex)
        {
            _runtime.Log($"[TRAY] Stop failed: {ex.Message}");
        }
    }

    private async Task QuitFromTrayAsync()
    {
        _allowRealClose = true;
        try { await _runtime.Engine.StopAllAsync(); } catch { }
        if (_trayIcon is not null) _trayIcon.Visible = false;
        await Dispatcher.InvokeAsync(() => Application.Current.Shutdown());
    }

    private void DisposeTrayIcon()
    {
        try
        {
            if (_trayIcon is not null)
            {
                _trayIcon.Visible = false;
                _trayIcon.Dispose();
                _trayIcon = null;
            }
            _trayMenu?.Dispose();
            _trayMenu = null;
            _trayDrawingIcon?.Dispose();
            _trayDrawingIcon = null;
        }
        catch { }
    }

    private sealed class TrayColorTable : System.Windows.Forms.ProfessionalColorTable
    {
        private static readonly System.Drawing.Color Surface = System.Drawing.Color.FromArgb(12, 20, 31);
        private static readonly System.Drawing.Color Hover = System.Drawing.Color.FromArgb(22, 38, 56);
        private static readonly System.Drawing.Color BorderColor = System.Drawing.Color.FromArgb(42, 64, 88);

        public override System.Drawing.Color ToolStripDropDownBackground => Surface;
        public override System.Drawing.Color ImageMarginGradientBegin => Surface;
        public override System.Drawing.Color ImageMarginGradientMiddle => Surface;
        public override System.Drawing.Color ImageMarginGradientEnd => Surface;
        public override System.Drawing.Color MenuBorder => BorderColor;
        public override System.Drawing.Color MenuItemBorder => BorderColor;
        public override System.Drawing.Color MenuItemSelected => Hover;
        public override System.Drawing.Color MenuItemSelectedGradientBegin => Hover;
        public override System.Drawing.Color MenuItemSelectedGradientEnd => Hover;
        public override System.Drawing.Color MenuItemPressedGradientBegin => Hover;
        public override System.Drawing.Color MenuItemPressedGradientEnd => Hover;
        public override System.Drawing.Color SeparatorDark => BorderColor;
        public override System.Drawing.Color SeparatorLight => BorderColor;
    }

    private static int ParseInt(string text, int fallback) => int.TryParse(text.Trim(), out var n) ? n : fallback;
    private static int ParsePositive(string text, int fallback) => Math.Max(1, ParseInt(text, fallback));
    private void ShowError(Exception ex) { _runtime.Log($"[ERROR] {ex.Message}"); MessageBox.Show(this, ex.Message, "TunnelGate", MessageBoxButton.OK, MessageBoxImage.Error); }

    private sealed class TunnelGridRow
    {
        public string Uid { get; set; } = "";
        public bool IsLive { get; set; }
        public string Live { get; set; } = "";
        public bool IsEnabled { get; set; }
        public string Enabled { get; set; } = "";
        public string Name { get; set; } = "";
        public string Mode { get; set; } = "";
        public string Route { get; set; } = "";
        public string Ssh { get; set; } = "";
    }
}
