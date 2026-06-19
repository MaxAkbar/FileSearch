using System.IO;
using System.Windows;

namespace FileSearch.Gui;

public partial class RenameFileWindow : Window
{
    public RenameFileWindow(string currentFileName)
    {
        InitializeComponent();
        NameBox.Text = currentFileName?.Trim() ?? string.Empty;
        Loaded += (_, _) =>
        {
            NameBox.Focus();
            NameBox.SelectAll();
        };
    }

    public string FileName { get; private set; } = string.Empty;

    private void OnRenameClick(object sender, RoutedEventArgs e)
    {
        var fileName = NameBox.Text.Trim();
        if (string.IsNullOrWhiteSpace(fileName))
        {
            ShowValidation("Enter a file name.");
            return;
        }

        if (fileName.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0 ||
            fileName.Contains(Path.DirectorySeparatorChar) ||
            fileName.Contains(Path.AltDirectorySeparatorChar))
        {
            ShowValidation("File names cannot contain path separators or invalid filename characters.");
            return;
        }

        FileName = fileName;
        DialogResult = true;
    }

    private void OnCancelClick(object sender, RoutedEventArgs e)
    {
        DialogResult = false;
    }

    private void ShowValidation(string message)
    {
        System.Windows.MessageBox.Show(
            this,
            message,
            "Rename File",
            MessageBoxButton.OK,
            MessageBoxImage.Warning);
        NameBox.Focus();
    }
}
