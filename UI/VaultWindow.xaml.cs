using System.Windows;
using System.Windows.Input;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Threading;
using TunnelGate.Core;

namespace TunnelGate.UI;

public partial class VaultWindow : Window
{
    private static readonly Brush StrengthOff = BrushFrom(0x24, 0x30, 0x44);
    private static readonly Brush StrengthBad = BrushFrom(0xFF, 0x6F, 0x7D);
    private static readonly Brush StrengthFair = BrushFrom(0xF4, 0xC3, 0x6B);
    private static readonly Brush StrengthGood = BrushFrom(0x6D, 0xC5, 0xFF);
    private static readonly Brush StrengthStrong = BrushFrom(0x58, 0xE6, 0x9A);
    private static readonly Brush Muted = BrushFrom(0x78, 0x88, 0x9F);

    private readonly SecureVault _vault;
    private readonly bool _create;
    private readonly DispatcherTimer _lockoutTimer;
    private bool _busy;
    private bool _blocked;
    private bool _revealPassword;
    private bool _revealConfirm;
    private bool _syncingReveal;
    private DateTimeOffset _lockUntilUtc;
    private string _actionText = string.Empty;

    public VaultWindow(SecureVault vault)
    {
        InitializeComponent();
        ThemeHelper.EnableDarkTitleBar(this);

        _vault = vault;
        _create = !vault.HasVault;
        _actionText = _create ? "Create secure vault" : "Unlock vault";

        ConfirmPanel.Visibility = _create ? Visibility.Visible : Visibility.Collapsed;
        StrengthPanel.Visibility = _create ? Visibility.Visible : Visibility.Collapsed;
        PasswordHintText.Visibility = _create ? Visibility.Visible : Visibility.Collapsed;
        ConfirmRevealButton.Visibility = _create ? Visibility.Visible : Visibility.Collapsed;

        TitleText.Text = _create ? "Create your secure vault" : "Welcome back";
        SubtitleText.Text = _create
            ? "Choose a master password to protect TunnelGate profiles, credentials and local tunnel settings."
            : "Enter your master password to unlock your encrypted TunnelGate configuration.";
        ActionButton.Content = _actionText;
        StatusText.Text = _create
            ? "A local AES-256-GCM vault will be created. Your master password is never stored in plaintext."
            : "Your saved profiles stay encrypted until this vault is unlocked.";

        _lockoutTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(1) };
        _lockoutTimer.Tick += LockoutTimer_Tick;

        if (!_create)
        {
            var lockout = vault.GetLockout();
            if (lockout.IsLocked)
                ApplyLockout(lockout.LockUntilUtc);
        }

        Loaded += (_, _) =>
        {
            FocusPrimaryInput();
            UpdateCapsLockWarning();
            UpdatePasswordVisuals();
        };

        Closed += (_, _) => _lockoutTimer.Stop();
    }

    private static SolidColorBrush BrushFrom(byte r, byte g, byte b)
    {
        var brush = new SolidColorBrush(Color.FromRgb(r, g, b));
        brush.Freeze();
        return brush;
    }

    private string CurrentPassword => _revealPassword ? PasswordRevealBox.Text : PasswordBox.Password;
    private string CurrentConfirm => _revealConfirm ? ConfirmRevealBox.Text : ConfirmBox.Password;

    private async void Action_Click(object sender, RoutedEventArgs e) => await SubmitAsync();

    private async void PasswordBox_KeyDown(object sender, KeyEventArgs e)
    {
        UpdateCapsLockWarning();
        if (e.Key == Key.Enter)
        {
            e.Handled = true;
            await SubmitAsync();
        }
    }

    private async void PasswordRevealBox_KeyDown(object sender, KeyEventArgs e)
    {
        UpdateCapsLockWarning();
        if (e.Key == Key.Enter)
        {
            e.Handled = true;
            await SubmitAsync();
        }
    }

    private async void Window_PreviewKeyDown(object sender, KeyEventArgs e)
    {
        UpdateCapsLockWarning();
        if (e.Key == Key.Escape && !_busy)
        {
            e.Handled = true;
            DialogResult = false;
            return;
        }

        if (e.Key == Key.Enter && !_busy && !_blocked && Keyboard.FocusedElement is not Button)
        {
            e.Handled = true;
            await SubmitAsync();
        }
    }

    private void PasswordBox_PasswordChanged(object sender, RoutedEventArgs e)
    {
        if (!_syncingReveal && PasswordRevealBox.Text != PasswordBox.Password)
        {
            _syncingReveal = true;
            PasswordRevealBox.Text = PasswordBox.Password;
            _syncingReveal = false;
        }
        UpdateCapsLockWarning();
        UpdatePasswordVisuals();
    }

    private void ConfirmBox_PasswordChanged(object sender, RoutedEventArgs e)
    {
        if (!_syncingReveal && ConfirmRevealBox.Text != ConfirmBox.Password)
        {
            _syncingReveal = true;
            ConfirmRevealBox.Text = ConfirmBox.Password;
            _syncingReveal = false;
        }
        UpdateCapsLockWarning();
        UpdatePasswordVisuals();
    }

    private void PasswordRevealBox_TextChanged(object sender, TextChangedEventArgs e)
    {
        if (_syncingReveal) return;
        _syncingReveal = true;
        if (PasswordBox.Password != PasswordRevealBox.Text)
            PasswordBox.Password = PasswordRevealBox.Text;
        _syncingReveal = false;
        UpdatePasswordVisuals();
    }

    private void ConfirmRevealBox_TextChanged(object sender, TextChangedEventArgs e)
    {
        if (_syncingReveal) return;
        _syncingReveal = true;
        if (ConfirmBox.Password != ConfirmRevealBox.Text)
            ConfirmBox.Password = ConfirmRevealBox.Text;
        _syncingReveal = false;
        UpdatePasswordVisuals();
    }

    private void PasswordRevealButton_Click(object sender, RoutedEventArgs e)
    {
        _revealPassword = !_revealPassword;
        SetRevealState(true, _revealPassword);
    }

    private void ConfirmRevealButton_Click(object sender, RoutedEventArgs e)
    {
        _revealConfirm = !_revealConfirm;
        SetRevealState(false, _revealConfirm);
    }

    private void SetRevealState(bool primary, bool reveal)
    {
        if (primary)
        {
            if (reveal)
            {
                PasswordRevealBox.Text = PasswordBox.Password;
                PasswordBox.Visibility = Visibility.Collapsed;
                PasswordRevealBox.Visibility = Visibility.Visible;
                PasswordRevealButton.Content = "Hide";
                PasswordRevealBox.Focus();
                PasswordRevealBox.CaretIndex = PasswordRevealBox.Text.Length;
            }
            else
            {
                PasswordBox.Password = PasswordRevealBox.Text;
                PasswordRevealBox.Visibility = Visibility.Collapsed;
                PasswordBox.Visibility = Visibility.Visible;
                PasswordRevealButton.Content = "Show";
                PasswordBox.Focus();
            }
        }
        else
        {
            if (reveal)
            {
                ConfirmRevealBox.Text = ConfirmBox.Password;
                ConfirmBox.Visibility = Visibility.Collapsed;
                ConfirmRevealBox.Visibility = Visibility.Visible;
                ConfirmRevealButton.Content = "Hide";
                ConfirmRevealBox.Focus();
                ConfirmRevealBox.CaretIndex = ConfirmRevealBox.Text.Length;
            }
            else
            {
                ConfirmBox.Password = ConfirmRevealBox.Text;
                ConfirmRevealBox.Visibility = Visibility.Collapsed;
                ConfirmBox.Visibility = Visibility.Visible;
                ConfirmRevealButton.Content = "Show";
                ConfirmBox.Focus();
            }
        }
    }

    private void FocusPrimaryInput()
    {
        if (_revealPassword)
        {
            PasswordRevealBox.Focus();
            PasswordRevealBox.CaretIndex = PasswordRevealBox.Text.Length;
        }
        else
        {
            PasswordBox.Focus();
        }
    }

    private void UpdateCapsLockWarning()
    {
        CapsWarningText.Visibility = Keyboard.IsKeyToggled(Key.CapsLock)
            ? Visibility.Visible
            : Visibility.Collapsed;
    }

    private void UpdatePasswordVisuals()
    {
        if (!_create) return;

        var password = CurrentPassword;
        var score = GetPasswordScore(password);
        var activeBrush = score switch
        {
            <= 1 => StrengthBad,
            2 => StrengthFair,
            3 => StrengthGood,
            _ => StrengthStrong
        };

        Strength1.Background = score >= 1 ? activeBrush : StrengthOff;
        Strength2.Background = score >= 2 ? activeBrush : StrengthOff;
        Strength3.Background = score >= 3 ? activeBrush : StrengthOff;
        Strength4.Background = score >= 4 ? activeBrush : StrengthOff;

        StrengthText.Text = password.Length == 0
            ? "Password strength"
            : score switch
            {
                <= 1 => "Weak",
                2 => "Fair",
                3 => "Strong",
                _ => "Excellent"
            };
        StrengthText.Foreground = password.Length == 0 ? Muted : activeBrush;

        var lengthOk = password.Length >= 6;
        LengthRequirementText.Text = lengthOk ? "✓ 6+ characters" : "○ 6+ characters";
        LengthRequirementText.Foreground = lengthOk ? StrengthStrong : Muted;

        var confirm = CurrentConfirm;
        if (confirm.Length == 0)
        {
            MatchRequirementText.Text = "Waiting for confirmation";
            MatchRequirementText.Foreground = Muted;
        }
        else if (password == confirm)
        {
            MatchRequirementText.Text = "✓ Passwords match";
            MatchRequirementText.Foreground = StrengthStrong;
        }
        else
        {
            MatchRequirementText.Text = "Passwords do not match";
            MatchRequirementText.Foreground = StrengthBad;
        }
    }

    private static int GetPasswordScore(string password)
    {
        if (string.IsNullOrEmpty(password)) return 0;
        if (password.Length < 6) return 1;

        var score = 1;
        if (password.Length >= 8) score++;
        if (password.Length >= 12) score++;

        var hasLower = password.Any(char.IsLower);
        var hasUpper = password.Any(char.IsUpper);
        var hasDigit = password.Any(char.IsDigit);
        var hasSymbol = password.Any(c => !char.IsLetterOrDigit(c));
        var classes = (hasLower ? 1 : 0) + (hasUpper ? 1 : 0) + (hasDigit ? 1 : 0) + (hasSymbol ? 1 : 0);
        if (classes >= 3) score++;

        return Math.Clamp(score, 1, 4);
    }

    private async Task SubmitAsync()
    {
        if (_busy || _blocked) return;

        HideMessage();
        var password = CurrentPassword;
        var confirm = CurrentConfirm;

        if (_create)
        {
            if (password.Length < 6)
            {
                ShowMessage("Use at least 6 characters for the master password.");
                FocusPrimaryInput();
                return;
            }

            if (password != confirm)
            {
                ShowMessage("The confirmation does not match the master password.");
                if (_revealConfirm) ConfirmRevealBox.Focus(); else ConfirmBox.Focus();
                return;
            }
        }
        else if (string.IsNullOrWhiteSpace(password))
        {
            ShowMessage("Enter your master password to unlock the vault.");
            FocusPrimaryInput();
            return;
        }

        SetBusy(true, _create ? "Creating and encrypting your local vault…" : "Decrypting and unlocking your vault…");
        try
        {
            if (_create)
            {
                await Task.Run(() => _vault.Create(password));
            }
            else
            {
                var unlocked = await Task.Run(() => _vault.Unlock(password));
                if (!unlocked)
                {
                    PasswordBox.Clear();
                    PasswordRevealBox.Clear();
                    var state = _vault.GetLockout();
                    if (state.IsLocked)
                    {
                        ApplyLockout(state.LockUntilUtc);
                    }
                    else
                    {
                        ShowMessage($"Wrong master password. {Math.Max(0, 5 - state.FailedCount)} attempt(s) remaining.");
                        FocusPrimaryInput();
                    }
                    return;
                }
            }

            StatusText.Text = _create
                ? "Vault created successfully. Opening TunnelGate…"
                : "Vault unlocked successfully. Opening TunnelGate…";
            DialogResult = true;
        }
        catch (Exception ex)
        {
            ShowMessage($"Vault operation failed: {ex.Message}");
        }
        finally
        {
            Mouse.OverrideCursor = null;
            if (IsVisible)
            {
                SetBusy(false, _create
                    ? "A local AES-256-GCM vault will be created. Your master password is never stored in plaintext."
                    : "Your saved profiles stay encrypted until this vault is unlocked.");
            }
        }
    }

    private void SetBusy(bool busy, string status)
    {
        _busy = busy;
        ActionButton.IsEnabled = !busy && !_blocked;
        PasswordBox.IsEnabled = !busy && !_blocked;
        PasswordRevealBox.IsEnabled = !busy && !_blocked;
        ConfirmBox.IsEnabled = !busy && !_blocked;
        ConfirmRevealBox.IsEnabled = !busy && !_blocked;
        PasswordRevealButton.IsEnabled = !busy && !_blocked;
        ConfirmRevealButton.IsEnabled = !busy && !_blocked;
        ActionButton.Content = busy ? (_create ? "Creating vault…" : "Unlocking…") : _actionText;
        StatusText.Text = status;
        Mouse.OverrideCursor = busy ? Cursors.Wait : null;
    }

    private void ApplyLockout(DateTimeOffset lockUntilUtc)
    {
        _blocked = true;
        _lockUntilUtc = lockUntilUtc;
        PasswordBox.Clear();
        PasswordRevealBox.Clear();
        ConfirmBox.Clear();
        ConfirmRevealBox.Clear();
        PasswordBox.IsEnabled = false;
        PasswordRevealBox.IsEnabled = false;
        ConfirmBox.IsEnabled = false;
        ConfirmRevealBox.IsEnabled = false;
        ActionButton.IsEnabled = false;
        PasswordRevealButton.IsEnabled = false;
        ConfirmRevealButton.IsEnabled = false;
        UpdateLockoutMessage();
        _lockoutTimer.Start();
    }

    private void LockoutTimer_Tick(object? sender, EventArgs e)
    {
        if (DateTimeOffset.UtcNow < _lockUntilUtc)
        {
            UpdateLockoutMessage();
            return;
        }

        _lockoutTimer.Stop();
        _blocked = false;
        HideMessage();
        PasswordBox.IsEnabled = true;
        PasswordRevealBox.IsEnabled = true;
        ConfirmBox.IsEnabled = true;
        ConfirmRevealBox.IsEnabled = true;
        ActionButton.IsEnabled = true;
        PasswordRevealButton.IsEnabled = true;
        ConfirmRevealButton.IsEnabled = true;
        StatusText.Text = "Lockout expired. Enter the master password to try again.";
        FocusPrimaryInput();
    }

    private void UpdateLockoutMessage()
    {
        var remaining = _lockUntilUtc - DateTimeOffset.UtcNow;
        if (remaining < TimeSpan.Zero) remaining = TimeSpan.Zero;
        var seconds = Math.Max(0, (int)Math.Ceiling(remaining.TotalSeconds));
        ShowMessage($"Too many failed attempts. Vault locked for {seconds}s.");
        StatusText.Text = "TunnelGate temporarily locked this vault to slow password guessing.";
    }

    private void ShowMessage(string message)
    {
        MessageText.Text = message;
        MessagePanel.Visibility = Visibility.Visible;
    }

    private void HideMessage()
    {
        MessageText.Text = string.Empty;
        MessagePanel.Visibility = Visibility.Collapsed;
    }

    private void Exit_Click(object sender, RoutedEventArgs e)
    {
        if (_busy) return;
        DialogResult = false;
    }
}
