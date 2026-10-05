using System.Windows;

namespace TunnelGate.UI;

public partial class ConfirmDialog : Window
{
    public ConfirmDialog(string title, string message, string confirmText = "Delete")
    {
        InitializeComponent();
        TunnelGate.Core.ThemeHelper.EnableDarkTitleBar(this);
        Title = title;
        TitleText.Text = title;
        MessageText.Text = message;
        ConfirmButton.Content = confirmText;
    }

    private void Confirm_Click(object sender, RoutedEventArgs e) => DialogResult = true;
    private void Cancel_Click(object sender, RoutedEventArgs e) => DialogResult = false;
}
