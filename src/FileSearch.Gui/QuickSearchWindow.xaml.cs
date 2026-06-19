using System;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Forms;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Threading;
using FileSearch.Gui.ViewModels;
using DataObject = System.Windows.DataObject;
using DragDropEffects = System.Windows.DragDropEffects;
using MouseEventArgs = System.Windows.Input.MouseEventArgs;
using Point = System.Windows.Point;

namespace FileSearch.Gui;

public partial class QuickSearchWindow : Window
{
    private const uint MonitorDefaultToNearest = 0x00000002;
    private const uint SwpNoZOrder = 0x0004;
    private const uint SwpNoActivate = 0x0010;

    private Point _dragStartPoint;
    private bool _isShowingFromHotkey;
    private int _externalDialogDepth;

    public QuickSearchWindow()
    {
        InitializeComponent();
        DataContextChanged += OnDataContextChanged;
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
        if (e.OldValue is QuickSearchViewModel oldViewModel)
        {
            oldViewModel.RequestHide -= OnRequestHide;
            oldViewModel.ExternalDialogOpened -= OnExternalDialogOpened;
            oldViewModel.ExternalDialogClosed -= OnExternalDialogClosed;
        }

        if (e.NewValue is QuickSearchViewModel newViewModel)
        {
            newViewModel.RequestHide += OnRequestHide;
            newViewModel.ExternalDialogOpened += OnExternalDialogOpened;
            newViewModel.ExternalDialogClosed += OnExternalDialogClosed;
        }
    }

    private void OnRequestHide(object? sender, EventArgs e) => DismissAndHide();

    private void OnWindowPreviewKeyDown(object sender, System.Windows.Input.KeyEventArgs e)
    {
        if (DataContext is not QuickSearchViewModel viewModel)
            return;

        if (e.Key == Key.Escape)
        {
            DismissAndHide();
            e.Handled = true;
            return;
        }

        if (e.Key == Key.Down && SearchBox.IsKeyboardFocusWithin)
        {
            FocusFirstResult();
            e.Handled = true;
            return;
        }

        if (e.Key == Key.Enter && viewModel.OpenResultCommand.CanExecute(null))
        {
            viewModel.OpenResultCommand.Execute(null);
            e.Handled = true;
            return;
        }

        var modifiers = Keyboard.Modifiers;
        if (modifiers == ModifierKeys.Control && e.Key == Key.R && viewModel.RevealResultCommand.CanExecute(null))
        {
            viewModel.RevealResultCommand.Execute(null);
            e.Handled = true;
            return;
        }

        if (modifiers == ModifierKeys.Control && e.Key == Key.C && !SearchBox.IsKeyboardFocusWithin && viewModel.CopyResultPathCommand.CanExecute(null))
        {
            viewModel.CopyResultPathCommand.Execute(null);
            e.Handled = true;
            return;
        }

        if (modifiers == ModifierKeys.Control && e.Key == Key.P && viewModel.PinResultCommand.CanExecute(null))
        {
            viewModel.PinResultCommand.Execute(null);
            e.Handled = true;
            return;
        }

        if ((e.Key == Key.F4 || (modifiers == ModifierKeys.Control && e.Key == Key.I)) &&
            viewModel.PreviewResultCommand.CanExecute(null))
        {
            viewModel.PreviewResultCommand.Execute(null);
            e.Handled = true;
        }
    }

    private void OnResultDoubleClick(object sender, MouseButtonEventArgs e)
    {
        if (DataContext is QuickSearchViewModel viewModel && viewModel.OpenResultCommand.CanExecute(null))
        {
            viewModel.OpenResultCommand.Execute(null);
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

        var current = e.GetPosition(ResultsList);
        if (Math.Abs(current.X - _dragStartPoint.X) < SystemParameters.MinimumHorizontalDragDistance &&
            Math.Abs(current.Y - _dragStartPoint.Y) < SystemParameters.MinimumVerticalDragDistance)
        {
            return;
        }

        if (ResultsList.SelectedItem is not FileResultViewModel file)
            return;

        var data = new DataObject(System.Windows.DataFormats.FileDrop, new[] { file.FullPath });
        DragDrop.DoDragDrop(ResultsList, data, DragDropEffects.Copy);
        DismissAndHide();
    }

    private void OnDeactivated(object sender, EventArgs e)
    {
        if (!_isShowingFromHotkey && _externalDialogDepth == 0)
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

        ResultsList.UpdateLayout();
        if (ResultsList.ItemContainerGenerator.ContainerFromIndex(ResultsList.SelectedIndex) is ListBoxItem item)
            item.Focus();
    }

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
