using System.Windows;

namespace FileSearch.Gui;

public partial class ScopeWindow : Window
{
    public ScopeWindow(string currentPattern)
    {
        InitializeComponent();
        PatternBox.Text = currentPattern?.Trim() ?? string.Empty;
        Loaded += (_, _) => NameBox.Focus();
    }

    public string ScopeName { get; private set; } = string.Empty;

    public string FileNamePattern { get; private set; } = string.Empty;

    private void OnSaveClick(object sender, RoutedEventArgs e)
    {
        var name = NameBox.Text.Trim();
        if (string.IsNullOrEmpty(name))
        {
            System.Windows.MessageBox.Show(
                this,
                "Enter a scope name.",
                "New Scope",
                MessageBoxButton.OK,
                MessageBoxImage.Warning);
            NameBox.Focus();
            return;
        }

        ScopeName = name;
        FileNamePattern = PatternBox.Text.Trim();
        DialogResult = true;
    }

    private void OnCancelClick(object sender, RoutedEventArgs e)
    {
        DialogResult = false;
    }
}
