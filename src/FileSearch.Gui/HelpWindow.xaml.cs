using System;
using System.IO;
using System.Windows;

namespace FileSearch.Gui;

public partial class HelpWindow : Window
{
    private readonly string _helpPath;

    public HelpWindow(string helpPath)
    {
        InitializeComponent();
        _helpPath = helpPath;
        Loaded += OnLoaded;
        Closed += OnClosed;
    }

    private async void OnLoaded(object sender, RoutedEventArgs e)
    {
        Loaded -= OnLoaded;

        try
        {
            await HelpWebView.EnsureCoreWebView2Async().ConfigureAwait(true);
            HelpWebView.CoreWebView2.NavigationStarting += (_, args) =>
            {
                if (!IsLocalHelpPage(args.Uri))
                    args.Cancel = true;
            };

            HelpWebView.Source = new Uri(_helpPath);
        }
        catch (Exception ex)
        {
            System.Windows.MessageBox.Show(
                this,
                $"The FileSearch help viewer could not be loaded.\n\n{ex.Message}",
                "FileSearch Help",
                MessageBoxButton.OK,
                MessageBoxImage.Warning);

            Close();
        }
    }

    private bool IsLocalHelpPage(string uri)
    {
        if (!Uri.TryCreate(uri, UriKind.Absolute, out var parsed) || !parsed.IsFile)
            return false;

        var helpFolder = Path.GetFullPath(Path.GetDirectoryName(_helpPath) ?? AppContext.BaseDirectory);
        var targetPath = Path.GetFullPath(parsed.LocalPath);

        return targetPath.StartsWith(helpFolder, StringComparison.OrdinalIgnoreCase);
    }

    private void OnCloseClick(object sender, RoutedEventArgs e) => Close();

    private void OnClosed(object? sender, EventArgs e)
    {
        HelpWebView.Dispose();
    }
}
