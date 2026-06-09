using System.Windows;
using FileSearch.Gui.ViewModels;
using Microsoft.Win32;

namespace FileSearch.Gui;

public partial class IndexedLocationsWindow : Window
{
    public IndexedLocationsWindow()
    {
        InitializeComponent();
    }

    private async void OnAddFolderClick(object sender, RoutedEventArgs e)
    {
        if (DataContext is not MainViewModel viewModel)
            return;

        var dialog = new OpenFolderDialog
        {
            Title = "Add folder to index",
            InitialDirectory = string.IsNullOrWhiteSpace(viewModel.SearchPath)
                ? Environment.CurrentDirectory
                : viewModel.SearchPath,
        };

        if (dialog.ShowDialog(this) == true)
            await viewModel.AddFolderToIndexAsync(dialog.FolderName).ConfigureAwait(true);
    }

    private void OnCloseClick(object sender, RoutedEventArgs e)
    {
        Close();
    }
}
