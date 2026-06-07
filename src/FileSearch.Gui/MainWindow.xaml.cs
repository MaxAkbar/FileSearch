using System;
using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using FileSearch.Gui.ViewModels;

namespace FileSearch.Gui;

public partial class MainWindow : Window
{
    public MainWindow()
    {
        InitializeComponent();
        UpdateNavSectionRows();
        DataContextChanged += OnDataContextChanged;
        SizeChanged += OnWindowSizeChanged;
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

    private void OnCopyMenuButtonClick(object sender, RoutedEventArgs e)
    {
        if (sender is System.Windows.Controls.Button { ContextMenu: { } menu } button)
        {
            menu.PlacementTarget = button;
            menu.IsOpen = true;
        }
    }

    // Collapsible sidebar sections: an expanded section's row takes the leftover
    // space (*), a collapsed one shrinks to its header (Auto) and is pushed to
    // the bottom of the sidebar.
    private void OnNavSectionToggled(object sender, RoutedEventArgs e) => UpdateNavSectionRows();

    private void UpdateNavSectionRows()
    {
        // Expanded/Collapsed can fire mid-XAML-load (the style sets IsExpanded)
        // before every named element is wired up; wait until they all exist.
        if (ScopesSection is null || RecentSection is null || SavedSection is null ||
            ScopesRow is null || RecentRow is null || SavedRow is null)
            return;

        // Only the LAST expanded section grows to fill the leftover space; the
        // others size to their content. This keeps fixed sections (Scopes) from
        // being stretched and clipped, while pushing collapsed sections — which
        // sit below the filler — down to the bottom of the sidebar.
        var sections = new[]
        {
            (Section: ScopesSection, Row: ScopesRow),
            (Section: RecentSection, Row: RecentRow),
            (Section: SavedSection, Row: SavedRow),
        };

        var fillIndex = -1;
        for (var i = 0; i < sections.Length; i++)
            if (sections[i].Section.IsExpanded)
                fillIndex = i;

        for (var i = 0; i < sections.Length; i++)
            sections[i].Row.Height = i == fillIndex
                ? new GridLength(1, GridUnitType.Star)
                : GridLength.Auto;
    }

    private void OnResultCardDoubleClick(object sender, MouseButtonEventArgs e)
    {
        if (sender is ListBoxItem { DataContext: FileResultViewModel file })
        {
            file.OpenCommand.Execute(null);
            e.Handled = true;
        }
    }

    private void OnPreviewSplitterDragDelta(object sender, DragDeltaEventArgs e)
    {
        if (DataContext is not MainViewModel viewModel || !viewModel.IsPreviewPaneVisible)
            return;

        viewModel.PreviewPaneWidth = Math.Clamp(
            viewModel.PreviewPaneWidth - e.HorizontalChange,
            MainViewModel.MinimumPreviewPaneWidth,
            GetAvailablePreviewPaneWidth());

        e.Handled = true;
    }

    private void OnDataContextChanged(object sender, DependencyPropertyChangedEventArgs e)
    {
        if (e.OldValue is MainViewModel oldViewModel)
            oldViewModel.PropertyChanged -= OnViewModelPropertyChanged;

        if (e.NewValue is MainViewModel newViewModel)
        {
            newViewModel.PropertyChanged += OnViewModelPropertyChanged;
            UpdatePreviewColumn(newViewModel);
        }
    }

    private void OnViewModelPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (sender is not MainViewModel viewModel)
            return;

        if (e.PropertyName is nameof(MainViewModel.IsPreviewPaneVisible) or nameof(MainViewModel.PreviewPaneWidth))
            UpdatePreviewColumn(viewModel);
    }

    private void OnWindowSizeChanged(object sender, SizeChangedEventArgs e)
    {
        if (DataContext is MainViewModel viewModel)
            UpdatePreviewColumn(viewModel);
    }

    private void UpdatePreviewColumn(MainViewModel viewModel)
    {
        if (!viewModel.IsPreviewPaneVisible)
        {
            PreviewColumn.MinWidth = 0;
            PreviewColumn.MaxWidth = 0;
            PreviewColumn.Width = new GridLength(0);
            return;
        }

        var width = Math.Clamp(
            viewModel.PreviewPaneWidth,
            MainViewModel.MinimumPreviewPaneWidth,
            GetAvailablePreviewPaneWidth());

        PreviewColumn.MinWidth = MainViewModel.MinimumPreviewPaneWidth;
        PreviewColumn.MaxWidth = GetAvailablePreviewPaneWidth();
        PreviewColumn.Width = new GridLength(width);
    }

    private double GetAvailablePreviewPaneWidth()
    {
        if (ShellRoot.ActualWidth <= 0)
            return MainViewModel.MaximumPreviewPaneWidth;

        var navigationWidth = ShellRoot.ColumnDefinitions[0].ActualWidth;
        var resultsMinWidth = ShellRoot.ColumnDefinitions[1].MinWidth;
        var splitterWidth = ShellRoot.ColumnDefinitions[2].ActualWidth;
        var available = ShellRoot.ActualWidth - navigationWidth - resultsMinWidth - splitterWidth;

        return Math.Clamp(
            available,
            MainViewModel.MinimumPreviewPaneWidth,
            MainViewModel.MaximumPreviewPaneWidth);
    }
}
