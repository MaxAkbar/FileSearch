using System;
using System.ComponentModel;
using System.IO;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Media;
using FileSearch.Gui.Settings;
using FileSearch.Gui.ViewModels;

namespace FileSearch.Gui;

public partial class MainWindow : Window
{
    private System.Windows.Point _resultsDragStartPoint;

    public MainWindow()
    {
        InitializeComponent();
        UpdateNavSectionRows();
        DataContextChanged += OnDataContextChanged;
        PreviewKeyDown += OnMainWindowPreviewKeyDown;
        ShellRoot.SizeChanged += OnShellRootSizeChanged;
    }

    private void OnExitClick(object sender, RoutedEventArgs e)
    {
        if (System.Windows.Application.Current is App app)
            app.RequestExit();
        else
            Close();
    }

    private void OnAboutClick(object sender, RoutedEventArgs e)
    {
        var aboutWindow = new AboutWindow
        {
            Owner = this,
        };

        aboutWindow.ShowDialog();
    }

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
            var helpWindow = new HelpWindow(helpPath)
            {
                Owner = this,
            };

            helpWindow.ShowDialog();
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

    private void OnSettingsClick(object sender, RoutedEventArgs e)
    {
        if (DataContext is not MainViewModel viewModel)
            return;

        var window = new SettingsWindow
        {
            Owner = this,
            DataContext = viewModel.Settings,
        };

        window.ShowDialog();
    }

    private void OnCopyMenuButtonClick(object sender, RoutedEventArgs e)
    {
        if (sender is System.Windows.Controls.Button { ContextMenu: { } menu } button)
        {
            menu.PlacementTarget = button;
            menu.IsOpen = true;
        }
    }

    private void OnExportMenuButtonClick(object sender, RoutedEventArgs e)
    {
        if (sender is System.Windows.Controls.Button { ContextMenu: { } menu } button)
        {
            menu.PlacementTarget = button;
            menu.IsOpen = true;
            e.Handled = true;
        }
    }

    private void OnAddScopeClick(object sender, RoutedEventArgs e)
    {
        if (DataContext is not MainViewModel viewModel)
            return;

        var scopeWindow = new ScopeWindow(viewModel.Search.FileNamePattern)
        {
            Owner = this,
        };

        if (scopeWindow.ShowDialog() == true)
            viewModel.History.SaveCustomScope(scopeWindow.ScopeName, scopeWindow.FileNamePattern);
    }

    private void OnRegexTesterClick(object sender, RoutedEventArgs e)
    {
        if (DataContext is not MainViewModel viewModel)
            return;

        var window = new RegexTesterWindow
        {
            Owner = this,
            DataContext = new RegexTesterViewModel(
                viewModel.Search.QueryText,
                viewModel.Search.MatchCase,
                viewModel.Search.PreviewContent),
        };

        window.ShowDialog();
    }

    private void OnManageIndexesClick(object sender, RoutedEventArgs e)
    {
        var window = new IndexedLocationsWindow
        {
            Owner = this,
            DataContext = DataContext,
        };

        window.ShowDialog();
    }

    private void OnWorkflowsClick(object sender, RoutedEventArgs e)
    {
        var window = new WorkflowsWindow
        {
            Owner = this,
            DataContext = DataContext,
        };

        window.ShowDialog();
    }

    // Collapsible sidebar sections: an expanded section's row takes the leftover
    // space (*), a collapsed one shrinks to its header (Auto) and is pushed to
    // the bottom of the sidebar.
    private void OnNavSectionToggled(object sender, RoutedEventArgs e) => UpdateNavSectionRows();

    private void UpdateNavSectionRows()
    {
        // Expanded/Collapsed can fire mid-XAML-load (the style sets IsExpanded)
        // before every named element is wired up; wait until they all exist.
        if (ScopesSection is null || WorkspacesSection is null || RecentSection is null || FavoritesSection is null || IndexSection is null || SavedSection is null ||
            ScopesRow is null || WorkspacesRow is null || RecentRow is null || FavoritesRow is null || IndexRow is null || SavedRow is null)
            return;

        // Only the LAST expanded section grows to fill the leftover space; the
        // others size to their content. This keeps fixed sections (Scopes) from
        // being stretched and clipped, while pushing collapsed sections — which
        // sit below the filler — down to the bottom of the sidebar.
        var sections = new[]
        {
            (Section: ScopesSection, Row: ScopesRow),
            (Section: WorkspacesSection, Row: WorkspacesRow),
            (Section: RecentSection, Row: RecentRow),
            (Section: FavoritesSection, Row: FavoritesRow),
            (Section: IndexSection, Row: IndexRow),
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

    private void OnMainWindowPreviewKeyDown(object sender, System.Windows.Input.KeyEventArgs e)
    {
        if (DataContext is not MainViewModel viewModel)
            return;

        foreach (var shortcut in viewModel.Settings.ShortcutBindings)
        {
            if (!MatchesShortcut(shortcut.Gesture, e))
                continue;

            if (ShouldIgnoreShortcut(shortcut.Action, shortcut.Gesture, e.OriginalSource))
                return;

            if (RequiresResultsFocus(shortcut.Action, shortcut.Gesture) && !ResultsList.IsKeyboardFocusWithin)
                return;

            if (!TryExecuteShortcut(shortcut.Action, viewModel))
                return;

            e.Handled = true;
            return;
        }
    }

    private bool TryExecuteShortcut(AppShortcutAction action, MainViewModel viewModel)
    {
        var search = viewModel.Search;
        var file = search.SelectedFile;

        switch (action)
        {
            case AppShortcutAction.FocusQuery:
                QueryBox.Focus();
                QueryBox.SelectAll();
                return true;

            case AppShortcutAction.FocusFolder:
                SearchPathBox.Focus();
                SearchPathBox.SelectAll();
                return true;

            case AppShortcutAction.StartSearch:
                return ExecuteCommand(search.SearchCommand);

            case AppShortcutAction.CancelSearch:
                return ExecuteCommand(search.CancelCommand);

            case AppShortcutAction.FocusResults:
                search.SelectedFile ??= search.FilesView.Cast<FileResultViewModel>().FirstOrDefault();
                ResultsList.Focus();
                if (search.SelectedFile is not null)
                    ResultsList.ScrollIntoView(search.SelectedFile);
                return true;

            case AppShortcutAction.TogglePreviewPane:
                return ExecuteCommand(search.TogglePreviewPaneCommand);

            case AppShortcutAction.OpenSelectedResult:
                return file is not null && ExecuteCommand(file.OpenCommand);

            case AppShortcutAction.RevealSelectedResult:
                return file is not null && ExecuteCommand(file.RevealInExplorerCommand);

            case AppShortcutAction.CopySelectedResultPath:
                return file is not null && ExecuteCommand(file.CopyPathCommand);

            case AppShortcutAction.PinSelectedResult:
                return file is not null && ExecuteCommand(search.TogglePinResultCommand, file);

            case AppShortcutAction.FavoriteSelectedResult:
                return file is not null && ExecuteCommand(search.ToggleFavoriteResultCommand, file);

            case AppShortcutAction.RenameSelectedResult:
                return file is not null && ExecuteCommand(search.RenameResultCommand, file);

            case AppShortcutAction.DeleteSelectedResult:
                return file is not null && ExecuteCommand(search.DeleteResultCommand, file);

            case AppShortcutAction.SaveWorkspace:
                return ExecuteCommand(search.SaveWorkspaceCommand);

            case AppShortcutAction.ClearResultFacets:
                return ExecuteCommand(search.ClearResultFacetsCommand);

            default:
                return false;
        }
    }

    private static bool ExecuteCommand(ICommand command, object? parameter = null)
    {
        if (!command.CanExecute(parameter))
            return false;

        command.Execute(parameter);
        return true;
    }

    private static bool MatchesShortcut(AppShortcutGesture gesture, System.Windows.Input.KeyEventArgs e)
    {
        var key = NormalizeKey(e);
        var modifiers = Keyboard.Modifiers;

        return gesture switch
        {
            AppShortcutGesture.CtrlF => modifiers == ModifierKeys.Control && key == Key.F,
            AppShortcutGesture.CtrlL => modifiers == ModifierKeys.Control && key == Key.L,
            AppShortcutGesture.CtrlEnter => modifiers == ModifierKeys.Control && key == Key.Return,
            AppShortcutGesture.Escape => modifiers == ModifierKeys.None && key == Key.Escape,
            AppShortcutGesture.CtrlR => modifiers == ModifierKeys.Control && key == Key.R,
            AppShortcutGesture.F8 => modifiers == ModifierKeys.None && key == Key.F8,
            AppShortcutGesture.Enter => modifiers == ModifierKeys.None && key == Key.Return,
            AppShortcutGesture.CtrlO => modifiers == ModifierKeys.Control && key == Key.O,
            AppShortcutGesture.CtrlE => modifiers == ModifierKeys.Control && key == Key.E,
            AppShortcutGesture.CtrlShiftC => modifiers == (ModifierKeys.Control | ModifierKeys.Shift) && key == Key.C,
            AppShortcutGesture.CtrlP => modifiers == ModifierKeys.Control && key == Key.P,
            AppShortcutGesture.CtrlShiftS => modifiers == (ModifierKeys.Control | ModifierKeys.Shift) && key == Key.S,
            AppShortcutGesture.F2 => modifiers == ModifierKeys.None && key == Key.F2,
            AppShortcutGesture.Delete => modifiers == ModifierKeys.None && key == Key.Delete,
            AppShortcutGesture.CtrlShiftW => modifiers == (ModifierKeys.Control | ModifierKeys.Shift) && key == Key.W,
            AppShortcutGesture.CtrlShiftBackspace => modifiers == (ModifierKeys.Control | ModifierKeys.Shift) && key == Key.Back,
            _ => false,
        };
    }

    private static Key NormalizeKey(System.Windows.Input.KeyEventArgs e)
    {
        if (e.Key == Key.System)
            return e.SystemKey;

        if (e.Key == Key.ImeProcessed)
            return e.ImeProcessedKey;

        return e.Key;
    }

    private static bool ShouldIgnoreShortcut(AppShortcutAction action, AppShortcutGesture gesture, object? source)
    {
        if (action == AppShortcutAction.CancelSearch || GestureHasModifiers(gesture))
            return false;

        return IsWithin<System.Windows.Controls.Primitives.TextBoxBase>(source)
            || IsWithin<PasswordBox>(source)
            || IsWithin<System.Windows.Controls.ComboBox>(source);
    }

    private static bool RequiresResultsFocus(AppShortcutAction action, AppShortcutGesture gesture) =>
        !GestureHasModifiers(gesture)
        && action is AppShortcutAction.OpenSelectedResult
            or AppShortcutAction.RenameSelectedResult
            or AppShortcutAction.DeleteSelectedResult;

    private static bool GestureHasModifiers(AppShortcutGesture gesture) =>
        gesture is AppShortcutGesture.CtrlF
            or AppShortcutGesture.CtrlL
            or AppShortcutGesture.CtrlEnter
            or AppShortcutGesture.CtrlR
            or AppShortcutGesture.CtrlO
            or AppShortcutGesture.CtrlE
            or AppShortcutGesture.CtrlShiftC
            or AppShortcutGesture.CtrlP
            or AppShortcutGesture.CtrlShiftS
            or AppShortcutGesture.CtrlShiftW
            or AppShortcutGesture.CtrlShiftBackspace;

    private static bool IsWithin<T>(object? source)
        where T : DependencyObject
    {
        var current = source as DependencyObject;
        while (current is not null)
        {
            if (current is T)
                return true;

            current = GetParent(current);
        }

        return false;
    }

    private static DependencyObject? GetParent(DependencyObject source)
    {
        try
        {
            var visualParent = VisualTreeHelper.GetParent(source);
            if (visualParent is not null)
                return visualParent;
        }
        catch (InvalidOperationException)
        {
        }
        catch (ArgumentException)
        {
        }

        if (source is FrameworkElement element)
            return element.Parent;

        if (source is FrameworkContentElement contentElement)
            return contentElement.Parent;

        return null;
    }

    private void OnResultsPreviewMouseMove(object sender, System.Windows.Input.MouseEventArgs e)
    {
        if (e.LeftButton != MouseButtonState.Pressed)
        {
            _resultsDragStartPoint = e.GetPosition(ResultsList);
            return;
        }

        var current = e.GetPosition(ResultsList);
        if (Math.Abs(current.X - _resultsDragStartPoint.X) < SystemParameters.MinimumHorizontalDragDistance &&
            Math.Abs(current.Y - _resultsDragStartPoint.Y) < SystemParameters.MinimumVerticalDragDistance)
        {
            return;
        }

        if (ResultsList.SelectedItem is not FileResultViewModel file)
            return;

        var data = new System.Windows.DataObject(System.Windows.DataFormats.FileDrop, new[] { file.FullPath });
        System.Windows.DragDrop.DoDragDrop(ResultsList, data, System.Windows.DragDropEffects.Copy);
    }

    private void OnPreviewSplitterDragDelta(object sender, DragDeltaEventArgs e)
    {
        if (DataContext is not MainViewModel viewModel || !viewModel.Search.IsPreviewPaneVisible)
            return;

        var availableWidth = GetAvailablePreviewPaneWidth();
        var minimumWidth = Math.Min(SearchViewModel.MinimumPreviewPaneWidth, availableWidth);

        viewModel.Search.PreviewPaneWidth = Math.Clamp(
            viewModel.Search.PreviewPaneWidth - e.HorizontalChange,
            minimumWidth,
            availableWidth);

        e.Handled = true;
    }

    private void OnDataContextChanged(object sender, DependencyPropertyChangedEventArgs e)
    {
        if (e.OldValue is MainViewModel oldViewModel)
            oldViewModel.Search.PropertyChanged -= OnSearchPropertyChanged;

        if (e.NewValue is MainViewModel newViewModel)
        {
            newViewModel.Search.PropertyChanged += OnSearchPropertyChanged;
            UpdatePreviewColumn(newViewModel.Search);
        }
    }

    private void OnSearchPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (sender is not SearchViewModel search)
            return;

        if (e.PropertyName is nameof(SearchViewModel.IsPreviewPaneVisible) or nameof(SearchViewModel.PreviewPaneWidth))
            UpdatePreviewColumn(search);
    }

    private void OnShellRootSizeChanged(object sender, SizeChangedEventArgs e)
    {
        if (DataContext is MainViewModel viewModel)
            UpdatePreviewColumn(viewModel.Search);
    }

    private void UpdatePreviewColumn(SearchViewModel search)
    {
        if (!search.IsPreviewPaneVisible)
        {
            PreviewColumn.MinWidth = 0;
            PreviewColumn.MaxWidth = 0;
            PreviewColumn.Width = new GridLength(0);
            return;
        }

        var availableWidth = GetAvailablePreviewPaneWidth();
        var minimumWidth = Math.Min(SearchViewModel.MinimumPreviewPaneWidth, availableWidth);
        var width = Math.Clamp(search.PreviewPaneWidth, minimumWidth, availableWidth);

        PreviewColumn.MinWidth = minimumWidth;
        PreviewColumn.MaxWidth = availableWidth;
        PreviewColumn.Width = new GridLength(width);
    }

    private double GetAvailablePreviewPaneWidth()
    {
        if (ShellRoot.ActualWidth <= 0)
            return SearchViewModel.MaximumPreviewPaneWidth;

        var navigationWidth = ShellRoot.ColumnDefinitions[0].ActualWidth;
        var resultsMinWidth = ShellRoot.ColumnDefinitions[1].MinWidth;
        var splitterWidth = ShellRoot.ColumnDefinitions[2].ActualWidth;
        var available = ShellRoot.ActualWidth - navigationWidth - resultsMinWidth - splitterWidth;

        return Math.Clamp(available, 0, SearchViewModel.MaximumPreviewPaneWidth);
    }
}
