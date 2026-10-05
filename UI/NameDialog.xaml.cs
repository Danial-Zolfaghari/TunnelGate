using System.Windows;
using System.Windows.Input;

namespace TunnelGate.UI;

public partial class NameDialog : Window
{
    public string Value => ValueBox.Text.Trim();
    public NameDialog(string title, string prompt, string initial = "")
    {
        InitializeComponent();
        TunnelGate.Core.ThemeHelper.EnableDarkTitleBar(this);
        Title = title;
        PromptText.Text = prompt;
        ValueBox.Text = initial;
        Loaded += (_, _) => { ValueBox.Focus(); ValueBox.SelectAll(); };
    }
    private void Save_Click(object sender, RoutedEventArgs e) => Save();
    private void ValueBox_KeyDown(object sender, KeyEventArgs e) { if (e.Key == Key.Enter) Save(); }
    private void Save() { if (string.IsNullOrWhiteSpace(Value)) { ErrorText.Text = "Name is required."; return; } DialogResult = true; }
    private void Cancel_Click(object sender, RoutedEventArgs e) => DialogResult = false;
}
