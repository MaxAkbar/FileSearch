using System;
using System.ComponentModel;
using System.Linq;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Forms;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Threading;
using FileSearch.Gui.Controls;
using FileSearch.Gui.Settings;
using FileSearch.Gui.ViewModels;
using ButtonBase = System.Windows.Controls.Primitives.ButtonBase;
using ComboBox = System.Windows.Controls.ComboBox;
using DataGrid = System.Windows.Controls.DataGrid;
using DataObject = System.Windows.DataObject;
using DragDropEffects = System.Windows.DragDropEffects;
using MouseEventArgs = System.Windows.Input.MouseEventArgs;
using Point = System.Windows.Point;
using ScrollBar = System.Windows.Controls.Primitives.ScrollBar;
using TextBoxBase = System.Windows.Controls.Primitives.TextBoxBase;

namespace FileSearch.Gui;

public partial class QuickSearchWindow : Window
{
    private const uint MonitorDefaultToNearest = 0x00000002;
    private const uint SwpNoZOrder = 0x0004;
    private const uint SwpNoActivate = 0x0010;

    private Point _dragStartPoint;
    private bool _isShowingFromHotkey;
    private int _externalDialogDepth;
    private bool _databaseFindVisible;
    private readonly double _normalMinimumHeight;
    private double? _heightBeforePreviewGrowth;
    private double? _automaticPreviewHeight;
    private bool _updatingPreviewLayout;
    private FileResultViewModel? _previewContextResult;
    private bool _previewContextVisible;
    private readonly ContentControl _inlineDocumentBody;
    private readonly DependencyPropertyDescriptor _inlineBodyDescriptor;
    private DatabasePreviewControl? _inlineDatabase;
    private DataGrid? _inlineDatabaseGrid;

    public QuickSearchWindow()
    {
        InitializeComponent();
        _normalMinimumHeight = MinHeight;
        DataContextChanged += OnDataContextChanged;
        SizeChanged += (_, _) =>
        {
            if (!_updatingPreviewLayout && _automaticPreviewHeight is { } automatic && Math.Abs(Height - automatic) > 1)
            {
                // A user resize owns the new height; closing the preview must not undo it.
                _heightBeforePreviewGrowth = _automaticPreviewHeight = null;
            }
            UpdatePreviewHeight();
        };
        QuickSearchLayout.SizeChanged += (_, _) => UpdatePreviewHeight();
        QuickSearchBar.SizeChanged += (_, _) => UpdatePreviewHeight();
        QuickScopeHeader.SizeChanged += (_, _) => UpdatePreviewHeight();
        _inlineDocumentBody = QuickFileDocumentPreview.FindName("Body") as ContentControl
            ?? throw new InvalidOperationException("The inline preview body is unavailable.");
        _inlineBodyDescriptor = DependencyPropertyDescriptor.FromProperty(ContentControl.ContentProperty, typeof(ContentControl))
            ?? throw new InvalidOperationException("The inline preview body cannot be observed.");
        _inlineBodyDescriptor.AddValueChanged(_inlineDocumentBody, OnInlineDocumentBodyChanged);
        QuickFileDocumentPreview.IsVisibleChanged += (_, _) => UpdatePreviewHeight();
        AddHandler(DatabasePreviewControl.FindVisibilityChangedEvent, new RoutedEventHandler((_, args) =>
        {
            if (args.OriginalSource is DatabasePreviewControl preview && ReferenceEquals(preview, _inlineDatabase))
                SetDatabaseFindLayout(preview.IsFindVisible);
        }));
        AddHandler(FileDocumentPreview.PreviewFindVisibilityChangedEvent, new RoutedEventHandler((_, args) =>
        {
            if (args.OriginalSource is FileDocumentPreview preview) SetDatabaseFindLayout(preview.IsPreviewFindVisible);
        }));
        Closed += (_, _) =>
        {
            if (DataContext is QuickSearchViewModel model) model.PropertyChanged -= OnPreviewContextChanged;
            _inlineBodyDescriptor.RemoveValueChanged(_inlineDocumentBody, OnInlineDocumentBodyChanged);
            if (_inlineDatabaseGrid is not null) _inlineDatabaseGrid.SizeChanged -= OnDatabaseGridSizeChanged;
        };
    }

    private void SetDatabaseFindLayout(bool visible)
    {
        _databaseFindVisible = visible;
        UpdatePreviewHeight();
    }

    private void OnInlineDocumentBodyChanged(object? sender, EventArgs args)
    {
        bool wasDatabase = _inlineDatabase is not null;
        if (_inlineDatabaseGrid is not null) _inlineDatabaseGrid.SizeChanged -= OnDatabaseGridSizeChanged;
        _inlineDatabase = _inlineDocumentBody.Content as DatabasePreviewControl;
        _inlineDatabaseGrid = _inlineDatabase is null ? null : FindDescendant<DataGrid>(_inlineDatabase);
        if (_inlineDatabaseGrid is not null) _inlineDatabaseGrid.SizeChanged += OnDatabaseGridSizeChanged;
        if (_inlineDatabase is not null) _databaseFindVisible = _inlineDatabase.IsFindVisible;
        else if (wasDatabase) _databaseFindVisible = QuickFileDocumentPreview.IsPreviewFindVisible;
        UpdatePreviewHeight();
    }

    private void OnDatabaseGridSizeChanged(object sender, SizeChangedEventArgs args) => UpdatePreviewHeight();

    private void UpdatePreviewHeight()
    {
        if (_updatingPreviewLayout || QuickSearchLayout.ActualHeight <= 0) return;
        _updatingPreviewLayout = true;
        try
        {
            // Reserve the measured header/chooser and enough database chrome for
            // a full record row. Ordinary document previews keep their compact sizing.
            double reserved = QuickSearchBar.ActualHeight + QuickSearchBar.Margin.Top + QuickSearchBar.Margin.Bottom +
                QuickScopeHeader.ActualHeight + QuickScopeHeader.Margin.Top + QuickScopeHeader.Margin.Bottom +
                InlinePreviewBorder.Margin.Top + InlinePreviewBorder.Margin.Bottom;
            bool databaseVisible = _inlineDatabase is not null && _previewContextVisible && QuickFileDocumentPreview.IsVisible;
            double databaseMinimum = 0;
            double minimumWindow = _databaseFindVisible ? Math.Max(_normalMinimumHeight, 540) : _normalMinimumHeight;
            if (databaseVisible)
            {
                databaseMinimum = _databaseFindVisible ? 340 : 300;
                if (_inlineDatabaseGrid is { ActualHeight: > 0 } grid && InlinePreviewBorder.ActualHeight > 0)
                {
                    double databaseChrome = InlinePreviewBorder.ActualHeight - grid.ActualHeight;
                    databaseMinimum = Math.Max(databaseMinimum, databaseChrome + 64);
                }
                databaseMinimum = Math.Min(420, Math.Ceiling(databaseMinimum));
                double windowChrome = Math.Max(0, ActualHeight - QuickSearchLayout.ActualHeight);
                minimumWindow = Math.Max(minimumWindow, Math.Ceiling(windowChrome + reserved + databaseMinimum));
            }
            ApplyPreviewMinimumHeight(minimumWindow);
            double available = Math.Max(0, QuickSearchLayout.ActualHeight - reserved);
            double desired = _databaseFindVisible ? 420 : Math.Clamp(ActualHeight * 0.6, 220, 420);
            InlinePreviewBorder.Height = Math.Min(Math.Max(desired, databaseMinimum), available);
        }
        finally { _updatingPreviewLayout = false; }
    }

    private void ApplyPreviewMinimumHeight(double minimum)
    {
        double current = double.IsFinite(Height) ? Height : ActualHeight;
        double target = current;
        if (_heightBeforePreviewGrowth is { } before && _automaticPreviewHeight is { } automatic && Math.Abs(current - automatic) < 1)
            target = Math.Max(before, minimum);
        else if (current < minimum)
        {
            _heightBeforePreviewGrowth ??= current;
            target = minimum;
        }
        MinHeight = minimum;
        if (Math.Abs(target - current) > .1)
        {
            _automaticPreviewHeight = target;
            Height = target;
        }
        if (_heightBeforePreviewGrowth is { } restored && target <= restored + .1 && minimum <= restored + .1)
            _heightBeforePreviewGrowth = _automaticPreviewHeight = null;
    }

    private static T? FindDescendant<T>(DependencyObject parent) where T : DependencyObject
    {
        for (int index = 0; index < System.Windows.Media.VisualTreeHelper.GetChildrenCount(parent); index++)
        {
            var child = System.Windows.Media.VisualTreeHelper.GetChild(parent, index);
            if (child is T match) return match;
            if (FindDescendant<T>(child) is { } nested) return nested;
        }
        return null;
    }

    private void OnPreviewContextChanged(object? sender, PropertyChangedEventArgs args)
    {
        if (args.PropertyName is not (nameof(QuickSearchViewModel.SelectedResult) or nameof(QuickSearchViewModel.IsPreviewVisible)) ||
            sender is not QuickSearchViewModel viewModel)
            return;
        if (ReferenceEquals(_previewContextResult, viewModel.SelectedResult) && _previewContextVisible == viewModel.IsPreviewVisible)
            return;
        _previewContextResult = viewModel.SelectedResult;
        _previewContextVisible = viewModel.IsPreviewVisible;
        SetDatabaseFindLayout(false);
    }

    public void ShowFromHotkey()
    {
        _isShowingFromHotkey = true;
        if (DataContext is QuickSearchViewModel viewModel)
            viewModel.PrepareForShow();

        PositionOnActiveMonitor();
        if (!IsVisible)
            Show();

        if (WindowState == WindowState.Minimized)
            WindowState = WindowState.Normal;

        PositionOnActiveMonitor();
        Activate();
        SearchBox.Focus();
        SearchBox.SelectAll();
        _isShowingFromHotkey = false;
    }

    private void OnDataContextChanged(object sender, DependencyPropertyChangedEventArgs e)
    {
        SetDatabaseFindLayout(false);
        _previewContextResult = (e.NewValue as QuickSearchViewModel)?.SelectedResult;
        _previewContextVisible = (e.NewValue as QuickSearchViewModel)?.IsPreviewVisible ?? false;
        UpdatePreviewHeight();
        if (e.OldValue is QuickSearchViewModel oldViewModel)
        {
            oldViewModel.RequestHide -= OnRequestHide;
            oldViewModel.ExternalDialogOpened -= OnExternalDialogOpened;
            oldViewModel.ExternalDialogClosed -= OnExternalDialogClosed;
            oldViewModel.PropertyChanged -= OnPreviewContextChanged;
        }

        if (e.NewValue is QuickSearchViewModel newViewModel)
        {
            newViewModel.RequestHide += OnRequestHide;
            newViewModel.ExternalDialogOpened += OnExternalDialogOpened;
            newViewModel.ExternalDialogClosed += OnExternalDialogClosed;
            newViewModel.PropertyChanged += OnPreviewContextChanged;
        }
    }

    private void OnRequestHide(object? sender, EventArgs e) => DismissAndHide();

    private void OnWindowPreviewKeyDown(object sender, System.Windows.Input.KeyEventArgs e)
    {
        e.Handled = TryHandleQuickKey(e.OriginalSource, NormalizeKey(e), Keyboard.Modifiers);
    }

    private bool TryHandleQuickKey(object? source, Key key, ModifierKeys modifiers)
    {
        if (DatabasePreviewControl.TryRouteFindKey(source, key, modifiers, out bool localHandled))
            return localHandled;
        if (FileDocumentPreview.TryRoutePreviewFindKey(source, key, modifiers, out bool previewHandled))
            return previewHandled;
        if (DataContext is not QuickSearchViewModel viewModel)
            return false;

        if (key == Key.Escape && modifiers == ModifierKeys.None)
        {
            if (QuickLocationScope.IsDropDownOpen) { QuickLocationScope.IsDropDownOpen = false; return true; }
            if (QuickLocationSelectionButton.IsChecked == true) { QuickLocationSelectionButton.IsChecked = false; QuickLocationSelectionButton.Focus(); return true; }
        }

        if (modifiers == ModifierKeys.None && key is Key.Up or Key.Down or Key.Home or Key.End or Key.PageUp or Key.PageDown &&
            (ReferenceEquals(source, ResultsList) || FindResultRow(source) is not null) &&
            !IsWithinInteractiveControl(source))
            viewModel.MarkSelectionExplicit();

        foreach (var action in s_shortcutActionOrder)
        {
            var gesture = viewModel.GetShortcut(action);
            if (!MatchesShortcut(gesture, key, modifiers))
                continue;

            if (ShouldIgnoreShortcut(action, gesture, source))
                return false;

            if (!TryExecuteShortcut(action, viewModel))
                return false;

            return true;
        }
        return false;
    }

    private void OnResultsPreviewMouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        if (DataContext is QuickSearchViewModel viewModel && FindResultRow(e.OriginalSource) is { DataContext: FileResultViewModel result })
        {
            ResultsList.SelectedItem = result;
            viewModel.MarkSelectionExplicit();
        }
    }

    private void OnResultDoubleClick(object sender, MouseButtonEventArgs e)
    {
        if (e.ChangedButton == MouseButton.Left && !IsWithinInteractiveControl(e.OriginalSource) &&
            FindResultRow(e.OriginalSource) is { DataContext: FileResultViewModel result } &&
            DataContext is QuickSearchViewModel viewModel && viewModel.OpenResultCommand.CanExecute(result))
        {
            viewModel.MarkSelectionExplicit();
            viewModel.OpenResultCommand.Execute(result);
            e.Handled = true;
        }
    }

    private void OnResultsPreviewMouseMove(object sender, MouseEventArgs e)
    {
        if (e.LeftButton != MouseButtonState.Pressed)
        {
            _dragStartPoint = e.GetPosition(ResultsList);
            return;
        }

        // The list's own scrollbars tunnel through this handler too; starting
        // an OLE drag would steal the mouse capture from the thumb and freeze
        // it mid-drag.
        if (IsWithinScrollBar(e.OriginalSource))
            return;

        var current = e.GetPosition(ResultsList);
        if (Math.Abs(current.X - _dragStartPoint.X) < SystemParameters.MinimumHorizontalDragDistance &&
            Math.Abs(current.Y - _dragStartPoint.Y) < SystemParameters.MinimumVerticalDragDistance)
        {
            return;
        }

        if (ResultsList.SelectedItem is not FileResultViewModel file || file.IsStoreMessage)
            return;

        var data = new DataObject(System.Windows.DataFormats.FileDrop, new[] { file.FullPath });
        DragDrop.DoDragDrop(ResultsList, data, DragDropEffects.Copy);
        DismissAndHide();
    }

    private static bool IsWithinScrollBar(object? source)
    {
        var current = source as DependencyObject;
        while (current is not null)
        {
            if (current is System.Windows.Controls.Primitives.ScrollBar)
                return true;

            current = current is FrameworkContentElement contentElement
                ? contentElement.Parent
                : System.Windows.Media.VisualTreeHelper.GetParent(current);
        }

        return false;
    }

    private void OnDeactivated(object sender, EventArgs e)
    {
        if (!_isShowingFromHotkey && _externalDialogDepth == 0 && !OwnedWindows.Cast<Window>().Any(window => window.IsVisible))
            DismissAndHide();
    }

    private void OnExternalDialogOpened(object? sender, EventArgs e) => _externalDialogDepth++;

    private void OnExternalDialogClosed(object? sender, EventArgs e)
    {
        if (_externalDialogDepth > 0)
            _externalDialogDepth--;

        if (_externalDialogDepth != 0 || !IsVisible)
            return;

        Dispatcher.BeginInvoke(() =>
        {
            if (!IsVisible)
                return;

            Activate();
            SearchBox.Focus();
        }, DispatcherPriority.ApplicationIdle);
    }

    private void DismissAndHide()
    {
        SetDatabaseFindLayout(false);
        if (DataContext is QuickSearchViewModel viewModel)
            viewModel.Dismiss();

        Hide();
    }

    private void FocusFirstResult()
    {
        if (ResultsList.Items.Count == 0)
            return;

        if (ResultsList.SelectedIndex < 0)
            ResultsList.SelectedIndex = 0;

        if (DataContext is QuickSearchViewModel viewModel)
            viewModel.MarkSelectionExplicit();
        ResultsList.ScrollIntoView(ResultsList.SelectedItem);
        ResultsList.UpdateLayout();
        if (ResultsList.ItemContainerGenerator.ContainerFromIndex(ResultsList.SelectedIndex) is ListBoxItem item)
            item.Focus();
    }

    private bool TryExecuteShortcut(QuickSearchShortcutAction action, QuickSearchViewModel viewModel)
    {
        switch (action)
        {
            case QuickSearchShortcutAction.Close:
                DismissAndHide();
                return true;

            case QuickSearchShortcutAction.FocusResults:
                FocusFirstResult();
                return ResultsList.Items.Count > 0;

            case QuickSearchShortcutAction.OpenSelectedResult:
                return ExecuteCommand(viewModel.OpenResultCommand);

            case QuickSearchShortcutAction.RevealSelectedResult:
                return ExecuteCommand(viewModel.RevealResultCommand);

            case QuickSearchShortcutAction.CopySelectedResultPath:
                return ExecuteCommand(viewModel.CopyResultPathCommand);

            case QuickSearchShortcutAction.PinSelectedResult:
                return ExecuteCommand(viewModel.PinResultCommand);

            case QuickSearchShortcutAction.PreviewSelectedResult:
                return ExecuteCommand(viewModel.PreviewResultCommand);

            default:
                return false;
        }
    }

    private static bool ExecuteCommand(ICommand command)
    {
        if (!command.CanExecute(null))
            return false;

        command.Execute(null);
        return true;
    }

    private bool ShouldIgnoreShortcut(QuickSearchShortcutAction action, AppShortcutGesture gesture, object? source)
    {
        if (action == QuickSearchShortcutAction.FocusResults &&
            gesture == AppShortcutGesture.Down &&
            !IsWithin(source, SearchBox))
        {
            return true;
        }

        if (gesture == AppShortcutGesture.Enter && IsWithinInteractiveControl(source))
            return true;

        return action == QuickSearchShortcutAction.CopySelectedResultPath && gesture == AppShortcutGesture.CtrlC &&
            FindAncestor<TextBoxBase>(source) is not null;
    }

    private static bool MatchesShortcut(AppShortcutGesture gesture, Key key, ModifierKeys modifiers)
    {
        return gesture switch
        {
            AppShortcutGesture.Down => modifiers == ModifierKeys.None && key == Key.Down,
            AppShortcutGesture.Escape => modifiers == ModifierKeys.None && key == Key.Escape,
            AppShortcutGesture.Enter => modifiers == ModifierKeys.None && key == Key.Return,
            AppShortcutGesture.CtrlR => modifiers == ModifierKeys.Control && key == Key.R,
            AppShortcutGesture.CtrlC => modifiers == ModifierKeys.Control && key == Key.C,
            AppShortcutGesture.CtrlP => modifiers == ModifierKeys.Control && key == Key.P,
            AppShortcutGesture.F4 => modifiers == ModifierKeys.None && key == Key.F4,
            AppShortcutGesture.CtrlI => modifiers == ModifierKeys.Control && key == Key.I,
            _ => false,
        };
    }

    private ListBoxItem? FindResultRow(object? source)
    {
        var row = FindAncestor<ListBoxItem>(source);
        return row is not null && ResultsList.ItemContainerGenerator.IndexFromContainer(row) >= 0 ? row : null;
    }

    private bool IsWithinInteractiveControl(object? source) =>
        FindAncestor<ButtonBase>(source) is not null || FindAncestor<ComboBox>(source) is not null ||
        FindAncestor<ComboBoxItem>(source) is not null ||
        FindAncestor<ScrollBar>(source) is not null ||
        FindAncestor<TextBoxBase>(source) is { } textBox && !ReferenceEquals(textBox, SearchBox);

    private static bool IsWithin(object? source, DependencyObject ancestor)
    {
        for (var current = source as DependencyObject; current is not null; current = InputParent(current))
            if (ReferenceEquals(current, ancestor)) return true;
        return false;
    }

    private static T? FindAncestor<T>(object? source) where T : DependencyObject
    {
        for (var current = source as DependencyObject; current is not null; current = InputParent(current))
            if (current is T match) return match;
        return null;
    }

    private static DependencyObject? InputParent(DependencyObject child) => child is FrameworkContentElement content
        ? content.Parent : child is System.Windows.Media.Visual or System.Windows.Media.Media3D.Visual3D
            ? System.Windows.Media.VisualTreeHelper.GetParent(child) ?? LogicalTreeHelper.GetParent(child)
            : LogicalTreeHelper.GetParent(child);

    private static Key NormalizeKey(System.Windows.Input.KeyEventArgs e)
    {
        if (e.Key == Key.System)
            return e.SystemKey;

        if (e.Key == Key.ImeProcessed)
            return e.ImeProcessedKey;

        return e.Key;
    }

    private static readonly QuickSearchShortcutAction[] s_shortcutActionOrder =
    [
        QuickSearchShortcutAction.Close,
        QuickSearchShortcutAction.FocusResults,
        QuickSearchShortcutAction.OpenSelectedResult,
        QuickSearchShortcutAction.RevealSelectedResult,
        QuickSearchShortcutAction.CopySelectedResultPath,
        QuickSearchShortcutAction.PinSelectedResult,
        QuickSearchShortcutAction.PreviewSelectedResult,
    ];

    private void PositionOnActiveMonitor()
    {
        var foreground = GetForegroundWindow();
        var monitor = foreground == IntPtr.Zero
            ? MonitorFromPoint(new NativePoint(System.Windows.Forms.Cursor.Position.X, System.Windows.Forms.Cursor.Position.Y), MonitorDefaultToNearest)
            : MonitorFromWindow(foreground, MonitorDefaultToNearest);
        var screen = foreground == IntPtr.Zero
            ? Screen.FromPoint(System.Windows.Forms.Cursor.Position)
            : Screen.FromHandle(foreground);
        var dpi = GetMonitorDpiScale(monitor);
        var width = ActualWidth > 0 ? ActualWidth : Width;
        var height = ActualHeight > 0 ? ActualHeight : Height;
        var widthPixels = Math.Max(1, (int)Math.Round(width * dpi.ScaleX));
        var heightPixels = Math.Max(1, (int)Math.Round(height * dpi.ScaleY));
        var workArea = screen.WorkingArea;
        var x = workArea.Left + Math.Max(0, (workArea.Width - widthPixels) / 2);
        var y = workArea.Top + Math.Max(24, (workArea.Height - heightPixels) / 5);

        var handle = new WindowInteropHelper(this).EnsureHandle();
        _ = SetWindowPos(handle, IntPtr.Zero, x, y, widthPixels, heightPixels, SwpNoZOrder | SwpNoActivate);
    }

    private static (double ScaleX, double ScaleY) GetMonitorDpiScale(IntPtr monitor)
    {
        if (monitor != IntPtr.Zero)
        {
            try
            {
                if (GetDpiForMonitor(monitor, MonitorDpiType.Effective, out var dpiX, out var dpiY) == 0)
                    return (dpiX / 96.0, dpiY / 96.0);
            }
            catch
            {
            }
        }

        return (1.0, 1.0);
    }

    [DllImport("user32.dll")]
    private static extern IntPtr GetForegroundWindow();

    [DllImport("user32.dll")]
    private static extern IntPtr MonitorFromWindow(IntPtr hwnd, uint dwFlags);

    [DllImport("user32.dll")]
    private static extern IntPtr MonitorFromPoint(NativePoint pt, uint dwFlags);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool SetWindowPos(IntPtr hWnd, IntPtr hWndInsertAfter, int x, int y, int cx, int cy, uint uFlags);

    [DllImport("shcore.dll")]
    private static extern int GetDpiForMonitor(IntPtr hmonitor, MonitorDpiType dpiType, out uint dpiX, out uint dpiY);

    private enum MonitorDpiType
    {
        Effective = 0,
    }

    [StructLayout(LayoutKind.Sequential)]
    private readonly struct NativePoint
    {
        public NativePoint(int x, int y)
        {
            X = x;
            Y = y;
        }

        public int X { get; }

        public int Y { get; }
    }
}
