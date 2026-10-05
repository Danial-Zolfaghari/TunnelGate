using System.Windows;
using TunnelGate.Core;

namespace TunnelGate.UI;

public partial class PasswordPromptDialog : Window
{
    private readonly bool _confirm;
    private bool _syncing;

    public string Password => ShowPasswordCheck.IsChecked == true ? PasswordRevealBox.Text : PasswordBox.Password;

    public PasswordPromptDialog(string title, string prompt, bool confirm)
    {
        InitializeComponent();
        ThemeHelper.EnableDarkTitleBar(this);
        Title = title;
        PromptText.Text = prompt;
        _confirm = confirm;
        ConfirmPanel.Visibility = confirm ? Visibility.Visible : Visibility.Collapsed;
        ShowPasswordCheck.Content = confirm ? "Show passwords" : "Show password";
        Loaded += (_, _) => PasswordBox.Focus();
    }

    private string ConfirmPassword => ShowPasswordCheck.IsChecked == true ? ConfirmRevealBox.Text : ConfirmBox.Password;

    private void Ok_Click(object sender, RoutedEventArgs e)
    {
        if (string.IsNullOrWhiteSpace(Password))
        {
            ErrorText.Text = "Password is required.";
            return;
        }

        if (_confirm && Password != ConfirmPassword)
        {
            ErrorText.Text = "Passwords do not match.";
            return;
        }

        DialogResult = true;
    }

    private void Cancel_Click(object sender, RoutedEventArgs e) => DialogResult = false;

    private void ShowPasswordCheck_Changed(object sender, RoutedEventArgs e)
    {
        var show = ShowPasswordCheck.IsChecked == true;
        if (show)
        {
            PasswordRevealBox.Text = PasswordBox.Password;
            PasswordBox.Visibility = Visibility.Collapsed;
            PasswordRevealBox.Visibility = Visibility.Visible;

            if (_confirm)
            {
                ConfirmRevealBox.Text = ConfirmBox.Password;
                ConfirmBox.Visibility = Visibility.Collapsed;
                ConfirmRevealBox.Visibility = Visibility.Visible;
            }
            PasswordRevealBox.Focus();
            PasswordRevealBox.CaretIndex = PasswordRevealBox.Text.Length;
        }
        else
        {
            PasswordBox.Password = PasswordRevealBox.Text;
            PasswordRevealBox.Visibility = Visibility.Collapsed;
            PasswordBox.Visibility = Visibility.Visible;

            if (_confirm)
            {
                ConfirmBox.Password = ConfirmRevealBox.Text;
                ConfirmRevealBox.Visibility = Visibility.Collapsed;
                ConfirmBox.Visibility = Visibility.Visible;
            }
            PasswordBox.Focus();
        }
    }

    private void PasswordBox_PasswordChanged(object sender, RoutedEventArgs e)
    {
        if (_syncing) return;
        _syncing = true;
        PasswordRevealBox.Text = PasswordBox.Password;
        _syncing = false;
    }

    private void PasswordRevealBox_TextChanged(object sender, System.Windows.Controls.TextChangedEventArgs e)
    {
        if (_syncing) return;
        _syncing = true;
        PasswordBox.Password = PasswordRevealBox.Text;
        _syncing = false;
    }

    private void ConfirmBox_PasswordChanged(object sender, RoutedEventArgs e)
    {
        if (_syncing) return;
        _syncing = true;
        ConfirmRevealBox.Text = ConfirmBox.Password;
        _syncing = false;
    }

    private void ConfirmRevealBox_TextChanged(object sender, System.Windows.Controls.TextChangedEventArgs e)
    {
        if (_syncing) return;
        _syncing = true;
        ConfirmBox.Password = ConfirmRevealBox.Text;
        _syncing = false;
    }
}
