using System.Windows;
using TunnelGate.Core;

namespace TunnelGate.UI;

public partial class ChangePasswordDialog : Window
{
    private readonly SecureVault _vault;
    private bool _syncing;

    public ChangePasswordDialog(SecureVault vault)
    {
        InitializeComponent();
        ThemeHelper.EnableDarkTitleBar(this);
        _vault = vault;
        Loaded += (_, _) => OldBox.Focus();
    }

    private string OldPassword => ShowPasswordCheck.IsChecked == true ? OldRevealBox.Text : OldBox.Password;
    private string NewPassword => ShowPasswordCheck.IsChecked == true ? NewRevealBox.Text : NewBox.Password;
    private string ConfirmPassword => ShowPasswordCheck.IsChecked == true ? ConfirmRevealBox.Text : ConfirmBox.Password;

    private void Change_Click(object sender, RoutedEventArgs e)
    {
        HideError();
        try
        {
            if (string.IsNullOrWhiteSpace(OldPassword)) { ShowError("Enter the current master password."); return; }
            if (NewPassword.Length < 6) { ShowError("Use at least 6 characters for the new password."); return; }
            if (NewPassword != ConfirmPassword) { ShowError("New passwords do not match."); return; }
            if (OldPassword == NewPassword) { ShowError("Choose a new password that is different from the current password."); return; }
            _vault.ChangePassword(OldPassword, NewPassword);
            DialogResult = true;
        }
        catch (Exception ex) { ShowError(ex.Message); }
    }

    private void ShowError(string message)
    {
        ErrorText.Text = message;
        ErrorPanel.Visibility = Visibility.Visible;
    }

    private void HideError()
    {
        ErrorText.Text = string.Empty;
        ErrorPanel.Visibility = Visibility.Collapsed;
    }

    private void Cancel_Click(object sender, RoutedEventArgs e) => DialogResult = false;

    private void ShowPasswordCheck_Changed(object sender, RoutedEventArgs e)
    {
        var show = ShowPasswordCheck.IsChecked == true;
        if (show)
        {
            OldRevealBox.Text = OldBox.Password;
            NewRevealBox.Text = NewBox.Password;
            ConfirmRevealBox.Text = ConfirmBox.Password;
            OldBox.Visibility = Visibility.Collapsed;
            NewBox.Visibility = Visibility.Collapsed;
            ConfirmBox.Visibility = Visibility.Collapsed;
            OldRevealBox.Visibility = Visibility.Visible;
            NewRevealBox.Visibility = Visibility.Visible;
            ConfirmRevealBox.Visibility = Visibility.Visible;
            OldRevealBox.Focus();
            OldRevealBox.CaretIndex = OldRevealBox.Text.Length;
        }
        else
        {
            OldBox.Password = OldRevealBox.Text;
            NewBox.Password = NewRevealBox.Text;
            ConfirmBox.Password = ConfirmRevealBox.Text;
            OldRevealBox.Visibility = Visibility.Collapsed;
            NewRevealBox.Visibility = Visibility.Collapsed;
            ConfirmRevealBox.Visibility = Visibility.Collapsed;
            OldBox.Visibility = Visibility.Visible;
            NewBox.Visibility = Visibility.Visible;
            ConfirmBox.Visibility = Visibility.Visible;
            OldBox.Focus();
        }
    }

    private void Sync(Action update)
    {
        if (_syncing) return;
        _syncing = true;
        update();
        _syncing = false;
    }

    private void OldBox_PasswordChanged(object sender, RoutedEventArgs e) => Sync(() => OldRevealBox.Text = OldBox.Password);
    private void NewBox_PasswordChanged(object sender, RoutedEventArgs e) => Sync(() => NewRevealBox.Text = NewBox.Password);
    private void ConfirmBox_PasswordChanged(object sender, RoutedEventArgs e) => Sync(() => ConfirmRevealBox.Text = ConfirmBox.Password);
    private void OldRevealBox_TextChanged(object sender, System.Windows.Controls.TextChangedEventArgs e) => Sync(() => OldBox.Password = OldRevealBox.Text);
    private void NewRevealBox_TextChanged(object sender, System.Windows.Controls.TextChangedEventArgs e) => Sync(() => NewBox.Password = NewRevealBox.Text);
    private void ConfirmRevealBox_TextChanged(object sender, System.Windows.Controls.TextChangedEventArgs e) => Sync(() => ConfirmBox.Password = ConfirmRevealBox.Text);
}
