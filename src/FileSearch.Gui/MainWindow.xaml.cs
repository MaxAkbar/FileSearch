using System;
using System.Diagnostics;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using FileSearch.Gui.ViewModels;

namespace FileSearch.Gui;

public partial class MainWindow : Window
{
    public MainWindow()
    {
        InitializeComponent();
    }

    private void OnExitClick(object sender, RoutedEventArgs e) => Close();

    private void OnAboutClick(object sender, RoutedEventArgs e) =>
        System.Windows.MessageBox.Show(
            this,
            "FileSearch\n\nA file-content search tool built on FileSearch.Core.",
            "About FileSearch",
            MessageBoxButton.OK,
            MessageBoxImage.Information);

    private void OnHelpClick(object sender, RoutedEventArgs e)
    {
        var helpPath = Path.Combine(AppContext.BaseDirectory, "Help", "index.html");
        if (!File.Exists(helpPath))
        {
            System.Windows.MessageBox.Show(
                this,
                $"The FileSearch help files could not be found.\n\nExpected location:\n{helpPath}",
                "FileSearch Help",
                MessageBoxButton.OK,
                MessageBoxImage.Warning);
            return;
        }

        try
        {
            Process.Start(new ProcessStartInfo(helpPath) { UseShellExecute = true });
        }
        catch (Exception ex)
        {
            System.Windows.MessageBox.Show(
                this,
                $"The FileSearch help files could not be opened.\n\n{ex.Message}",
                "FileSearch Help",
                MessageBoxButton.OK,
                MessageBoxImage.Warning);
        }
    }

    private void OnResultRowDoubleClick(object sender, MouseButtonEventArgs e)
    {
        if (sender is DataGridRow { DataContext: FileResultViewModel file })
        {
            file.OpenCommand.Execute(null);
            e.Handled = true;
        }
    }

    /// <summary>
    /// Editable ComboBox + history dropdown.
    ///
    /// We bind <c>Text</c> with <c>UpdateSourceTrigger=LostFocus</c> so that
    /// the transient empty <c>Text</c> WPF emits during a dropdown
    /// transition never reaches the bound model property. Since LostFocus
    /// doesn't fire when the user picks from the dropdown, we commit the
    /// binding ourselves here once <c>Text</c> has settled to the picked
    /// value.
    /// </summary>
    private void OnHistoryComboBoxSelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (sender is not System.Windows.Controls.ComboBox cb) return;
        if (e.AddedItems.Count == 0) return;

        // Defer until after WPF finishes synchronising Text with the new
        // SelectedItem — otherwise we'd commit a stale value.
        cb.Dispatcher.BeginInvoke(() =>
        {
            cb.GetBindingExpression(System.Windows.Controls.ComboBox.TextProperty)?.UpdateSource();
        }, System.Windows.Threading.DispatcherPriority.ContextIdle);
    }

    /// <summary>
    /// With <c>LostFocus</c> source-update, pressing Enter inside the
    /// ComboBox would run the search before <c>Text</c> reaches the
    /// model. Commit the binding first so the SearchCommand sees the
    /// current text the user is looking at.
    /// </summary>
    private void OnHistoryComboBoxPreviewKeyDown(object sender, System.Windows.Input.KeyEventArgs e)
    {
        if (e.Key == Key.Enter && sender is System.Windows.Controls.ComboBox cb)
            cb.GetBindingExpression(System.Windows.Controls.ComboBox.TextProperty)?.UpdateSource();
    }
}
