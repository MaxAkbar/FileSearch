using System.Data;
using System.Globalization;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using System.Windows.Input;
using FileSearch.Core.Databases;
using FileSearch.Core.Engine;
using FileSearch.Core.Queries;
using FileSearch.Gui.Highlighting;
using Binding = System.Windows.Data.Binding;
using Button = System.Windows.Controls.Button;
using CheckBox = System.Windows.Controls.CheckBox;
using ComboBox = System.Windows.Controls.ComboBox;
using Control = System.Windows.Controls.Control;
using TextBox = System.Windows.Controls.TextBox;

namespace FileSearch.Gui.Controls;

internal sealed record DatabasePreviewFindState(string Table, string Fingerprint, DatabaseFindOptions Options, DatabaseFindResult Result, bool IsOpen, bool OptionsOpen);
internal sealed record DatabaseFindModeOption(DatabaseFindMode Value, string Label);
internal sealed record DatabaseFindColumnOption(string? Column, string Label);

public sealed partial class DatabasePreviewControl
{
    private readonly TextBox _findQuery = new() { Name = "DatabaseFindTextBox", Width = 170, Height = 28, MaxLength = 2048, Margin = new Thickness(4, 0, 4, 2) };
    private readonly ComboBox _findMode = new() { Name = "DatabaseFindModeSelector", Width = 112, Height = 28, DisplayMemberPath = "Label", SelectedValuePath = "Value", Margin = new Thickness(0, 0, 4, 2) };
    private readonly ComboBox _findColumns = new() { Name = "DatabaseFindColumnSelector", MinWidth = 100, MaxWidth = 180, Height = 28, DisplayMemberPath = "Label", SelectedValuePath = "Column", Margin = new Thickness(0, 0, 4, 2) };
    private readonly CheckBox _findCase = new() { Name = "DatabaseFindMatchCase", Content = "Match case", VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(4, 0, 0, 2) };
    private readonly TextBlock _findStatus = new() { FontSize = 11.5, TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 3, 0, 2) };
    private StackPanel _findPanel = null!;
    private StackPanel _findOptionsPanel = null!;
    private Button _findOptionsButton = null!;
    private Button _findButton = null!, _findStop = null!, _findPrevious = null!, _findNext = null!;
    private CancellationTokenSource? _findLoad, _findNavigation;
    private DatabaseFindResult? _findResult;
    private DatabaseFindOptions? _findOptions;
    private readonly Dictionary<(long Row, string Column), DatabaseCellMatch> _findHighlights = new();
    private int _findGeneration, _findMatchIndex = -1;
    private bool _finding, _updatingFind, _findStopped;

    private static readonly DependencyProperty FindHighlightVersionProperty = DependencyProperty.Register(
        nameof(FindHighlightVersion), typeof(int), typeof(DatabasePreviewControl), new PropertyMetadata(0));
    public int FindHighlightVersion => (int)GetValue(FindHighlightVersionProperty);
    public string FindState { get; private set; } = "Idle";
    public string FindStatusText => _findStatus.Text;
    public int FindMatchCount => _findResult?.Matches.Count ?? 0;
    public int CurrentFindMatchNumber => _findMatchIndex + 1;
    public bool IsFindVisible => _findPanel.Visibility == Visibility.Visible;
    public static readonly RoutedEvent FindVisibilityChangedEvent = EventManager.RegisterRoutedEvent(
        nameof(FindVisibilityChanged), RoutingStrategy.Bubble, typeof(RoutedEventHandler), typeof(DatabasePreviewControl));
    public event RoutedEventHandler FindVisibilityChanged
    {
        add => AddHandler(FindVisibilityChangedEvent, value);
        remove => RemoveHandler(FindVisibilityChangedEvent, value);
    }

    private StackPanel CreateFindPanel()
    {
        _findPanel = new StackPanel { Name = "DatabaseFindPanel", Margin = new Thickness(6, 0, 6, 4), Visibility = Visibility.Collapsed };
        SetDock(_findPanel, Dock.Top);
        var input = new WrapPanel();
        var label = new TextBlock { Text = "Find in table", VerticalAlignment = VerticalAlignment.Center };
        label.SetResourceReference(TextBlock.ForegroundProperty, "Atlas.InkBrush");
        input.Children.Add(label);
        input.Children.Add(_findQuery);
        _findButton = AddButton(input, "Find", () => _ = FindInTableAsync());
        _findStop = AddButton(input, "Stop", CancelFind);
        _findStop.Name = "DatabaseFindStopButton";
        var options = new WrapPanel();
        _findMode.ItemsSource = new[]
        {
            new DatabaseFindModeOption(DatabaseFindMode.AllTerms, "All terms"),
            new DatabaseFindModeOption(DatabaseFindMode.AnyTerm, "Either term"),
            new DatabaseFindModeOption(DatabaseFindMode.Phrase, "Phrase"),
        };
        _findMode.SelectedIndex = 0;
        options.Children.Add(_findMode); options.Children.Add(_findColumns); options.Children.Add(_findCase);
        var navigation = new WrapPanel();
        _findPrevious = AddButton(navigation, "‹ Match", () => _ = MoveFindMatchAsync(-1));
        _findNext = AddButton(navigation, "Match ›", () => _ = MoveFindMatchAsync(1));
        _findOptionsButton = AddButton(navigation, "Find options", () =>
        {
            _findOptionsPanel.Visibility = _findOptionsPanel.Visibility == Visibility.Visible ? Visibility.Collapsed : Visibility.Visible;
            _ = KeepCurrentCellVisibleAsync();
        });
        _findOptionsButton.Name = "DatabaseFindOptionsButton";
        var budget = new TextBlock
        {
            Text = "Selected table · Per cell · Up to 100,000 rows / 1,000 matching cells / 15 s",
            FontSize = 11,
            TextWrapping = TextWrapping.Wrap,
            Margin = new Thickness(0, 3, 0, 0),
            ToolTip = "Find stops at 100,000 rows, 250,000 populated scalar cells, 16 Mi characters, 1,000 matching cells or 15 seconds. Only the first 16,384 characters of a cell are searched. Limits and shortened values are reported as partial coverage.",
        };
        foreach (var element in new[] { _findStatus, budget }) element.SetResourceReference(TextBlock.ForegroundProperty, "Atlas.Ink2Brush");
        _findOptionsButton.ToolTip = "Matching and column options. " + budget.ToolTip;
        _findButton.ToolTip = budget.ToolTip;
        _findOptionsPanel = new StackPanel { Visibility = Visibility.Collapsed };
        _findOptionsPanel.Children.Add(options); _findOptionsPanel.Children.Add(budget);
        _findPanel.Children.Add(input); _findPanel.Children.Add(navigation);
        _findPanel.Children.Add(_findOptionsPanel); _findPanel.Children.Add(_findStatus);
        foreach (var control in new Control[] { _findQuery, _findMode, _findColumns, _findCase })
        {
            control.SetResourceReference(Control.ForegroundProperty, "Atlas.InkBrush");
            control.SetResourceReference(Control.BackgroundProperty, "Atlas.SurfaceBrush");
        }
        _findQuery.ToolTip = "Literal text in a single cell. All terms and Either term split on whitespace; choose Phrase for the whole text. Quotes and AND/OR are ordinary text here.";
        _findMode.ToolTip = "Combine literal terms within one cell. Values in different columns are not combined.";
        _findColumns.ToolTip = "Search all supported columns, or only the selected column, in the selected table.";
        System.Windows.Automation.AutomationProperties.SetName(_findQuery, "Find text in database table");
        System.Windows.Automation.AutomationProperties.SetName(_findMode, "Database Find matching mode");
        System.Windows.Automation.AutomationProperties.SetName(_findColumns, "Database Find column");
        System.Windows.Automation.AutomationProperties.SetName(_findCase, "Database Find match case");
        System.Windows.Automation.AutomationProperties.SetName(_findOptionsButton, "Database Find matching and column options");
        System.Windows.Automation.AutomationProperties.SetLiveSetting(_findStatus, System.Windows.Automation.AutomationLiveSetting.Polite);
        HitHighlight.SetFilterText(_grid, string.Empty);
        _findQuery.TextChanged += (_, _) => FindInputsChanged();
        _findMode.SelectionChanged += (_, _) => FindInputsChanged();
        _findColumns.SelectionChanged += (_, _) => FindInputsChanged();
        _findCase.Checked += (_, _) => FindInputsChanged(); _findCase.Unchecked += (_, _) => FindInputsChanged();
        PreviewKeyDown += (_, args) =>
        {
            if (TryRouteFindKey(args.OriginalSource, args.Key, Keyboard.Modifiers, out bool handled)) args.Handled = handled;
        };
        UpdateFindControls();
        return _findPanel;
    }

    // Window-level shortcuts tunnel before the preview's own key handler.
    // Reserve local editing/copy keys before Main/Quick can launch a file or search.
    internal static bool TryRouteFindKey(object? source, Key key, ModifierKeys modifiers, out bool handled)
    {
        handled = false;
        var current = source as DependencyObject;
        while (current is not null && current is not DatabasePreviewControl)
            current = KeyParent(current);
        if (current is not DatabasePreviewControl preview || preview._disposed) return false;
        if (key == Key.F && modifiers == ModifierKeys.Control)
        { preview.ShowFind(); handled = true; return true; }
        if (key == Key.F3 && modifiers is ModifierKeys.None or ModifierKeys.Shift)
        {
            if (preview._findResult is null) preview.ShowFind();
            else { preview.SetFindVisible(true); _ = preview.MoveFindMatchAsync(modifiers == ModifierKeys.Shift ? -1 : 1); }
            handled = true; return true;
        }
        if (key == Key.Escape && modifiers == ModifierKeys.None && (preview._finding || preview._findPanel.Visibility == Visibility.Visible))
        {
            if (preview._finding) preview.CancelFind(); else { preview.ToggleFind(); preview._grid.Focus(); }
            handled = true; return true;
        }
        if (key == Key.Enter && modifiers is ModifierKeys.None or ModifierKeys.Control)
        {
            current = source as DependencyObject;
            while (current is not null && !ReferenceEquals(current, preview._findQuery)) current = KeyParent(current);
            if (current is not null) { _ = preview.FindInTableAsync(); handled = true; return true; }
            return modifiers == ModifierKeys.None;
        }
        return key == Key.C && modifiers == ModifierKeys.Control;
    }

    private static DependencyObject? KeyParent(DependencyObject child) => child is FrameworkContentElement content
        ? content.Parent : System.Windows.Media.VisualTreeHelper.GetParent(child) ?? LogicalTreeHelper.GetParent(child);

    private void ShowFind()
    {
        SetFindVisible(true);
        _findQuery.Focus(); _findQuery.SelectAll();
        _ = KeepCurrentCellVisibleAsync();
    }

    private void ToggleFind()
    {
        if (_findPanel.Visibility != Visibility.Visible) { ShowFind(); return; }
        if (_finding) CancelFind();
        SetFindVisible(false);
        _ = KeepCurrentCellVisibleAsync();
    }

    private void SetFindColumns()
    {
        _updatingFind = true;
        var columns = new List<DatabaseFindColumnOption> { new(null, "All columns") };
        if (_tables.SelectedItem is DatabaseTable table)
            columns.AddRange(table.Columns.Select(column => new DatabaseFindColumnOption(column.Name, "Column: " + column.Name)));
        _findColumns.ItemsSource = columns; _findColumns.SelectedIndex = 0;
        _updatingFind = false;
        UpdateFindControls();
    }

    private void FindInputsChanged()
    {
        if (!_updatingFind) ResetFind("Find settings changed. Choose Find to search the table again.");
    }

    private void ResetFind(string message)
    {
        ++_findGeneration;
        _findLoad?.Cancel(); _findLoad?.Dispose(); _findLoad = null;
        _findNavigation?.Cancel(); _findNavigation?.Dispose(); _findNavigation = null;
        _finding = false; _findStopped = false;
        _findResult = null; _findOptions = null; _findMatchIndex = -1;
        _findHighlights.Clear(); SetValue(FindHighlightVersionProperty, FindHighlightVersion + 1);
        FindState = "Idle"; _findStatus.Text = message;
        UpdateFindControls();
    }

    public void CancelFind()
    {
        if (!_finding) return;
        _findStopped = true; _findLoad?.Cancel();
    }

    private void CancelFindForLifetime()
    {
        ResetFind(string.Empty);
        SetFindVisible(false);
    }

    private void SetFindVisible(bool visible)
    {
        if (IsFindVisible == visible) return;
        _findPanel.Visibility = visible ? Visibility.Visible : Visibility.Collapsed;
        RaiseEvent(new RoutedEventArgs(FindVisibilityChangedEvent, this));
    }

    public async Task FindInTableAsync()
    {
        if (_disposed || _session is not { } session || _tables.SelectedItem is not DatabaseTable table) return;
        ResetFind(string.Empty);
        SetFindVisible(true);
        var options = new DatabaseFindOptions(_findQuery.Text,
            _findMode.SelectedValue is DatabaseFindMode mode ? mode : DatabaseFindMode.AllTerms,
            (_findColumns.SelectedItem as DatabaseFindColumnOption)?.Column, _findCase.IsChecked == true);
        _findOptions = options;
        int generation = _findGeneration;
        using var find = CancellationTokenSource.CreateLinkedTokenSource(_lifetime.Token);
        _findLoad = find;
        var token = find.Token; find.CancelAfter(TimeSpan.FromSeconds(15));
        _findOptionsPanel.Visibility = Visibility.Collapsed;
        _finding = true; FindState = "Searching";
        _findStatus.Text = "Searching the selected table beyond this page…";
        UpdateFindControls();
        try
        {
            var result = await Task.Run(() => session.FindAsync(table.Name, options, token), token);
            token.ThrowIfCancellationRequested();
            if (generation != _findGeneration || _disposed) return;
            _findResult = result;
            foreach (var match in result.Matches) _findHighlights[(match.Location.RowNumber, match.Location.Column)] = match;
            SetValue(FindHighlightVersionProperty, FindHighlightVersion + 1);
            FindState = !result.IsComplete ? "Partial" : result.Matches.Count == 0 ? "NoMatches" : "Ready";
            _finding = false;
            UpdateFindControls(); UpdateFindStatus();
            if (result.Matches.Count > 0) await NavigateFindMatchAsync(result, 0);
        }
        catch (OperationCanceledException)
        {
            if (generation != _findGeneration || _disposed) return;
            FindState = _findStopped ? "Cancelled" : "Partial";
            _findStatus.Text = _findStopped ? "Find canceled. The table may still contain matches; search again to check it."
                : "Find stopped after 15 seconds. Coverage is incomplete; narrow the column or search text and try again.";
        }
        catch (Exception error)
        {
            if (generation != _findGeneration || _disposed) return;
            FindState = "Unavailable";
            _findStatus.Text = error is InvalidDataException ? error.Message : error is ArgumentException
                ? (string.IsNullOrWhiteSpace(options.Query) ? "Enter text to find." : "Find supports 1–2,048 characters and up to 64 literal terms. Choose Phrase for longer literal text.")
                : "The table could not be searched. Reopen a stable database preview.";
        }
        finally
        {
            if (generation == _findGeneration && !_disposed)
            { _finding = false; if (ReferenceEquals(_findLoad, find)) _findLoad = null; UpdateFindControls(); }
        }
    }

    public async Task MoveFindMatchAsync(int direction)
    {
        if (_disposed || _finding || _findResult is not { Matches.Count: > 0 } result) return;
        int index;
        if (_findMatchIndex >= 0) index = (_findMatchIndex + (direction < 0 ? -1 : 1) + result.Matches.Count) % result.Matches.Count;
        else if (direction < 0) { index = result.Matches.ToList().FindLastIndex(match => CompareToCurrent(match.Location) < 0); if (index < 0) index = result.Matches.Count - 1; }
        else { index = result.Matches.ToList().FindIndex(match => CompareToCurrent(match.Location) > 0); if (index < 0) index = 0; }
        await NavigateFindMatchAsync(result, index);
    }

    private async Task NavigateFindMatchAsync(DatabaseFindResult result, int index)
    {
        var match = result.Matches[index];
        _findNavigation?.Cancel(); _findNavigation?.Dispose();
        _findNavigation = CancellationTokenSource.CreateLinkedTokenSource(_lifetime.Token);
        var navigation = _findNavigation;
        var token = navigation.Token;
        int generation = _findGeneration;
        bool pageLoaded = await LoadPageAsync(match.Location.RowNumber, token, match.Location.Column);
        if (!pageLoaded || generation != _findGeneration || !ReferenceEquals(_findNavigation, navigation) || token.IsCancellationRequested || _disposed || PreviewState != "Ready") return;
        _findMatchIndex = index;
        UpdateFindStatus(); UpdateFindControls();
    }

    private int CompareToCurrent(DatabaseLocation location)
    {
        int row = location.RowNumber.CompareTo(_row);
        if (row != 0 || _tables.SelectedItem is not DatabaseTable table) return row;
        int selected = table.Columns.ToList().FindIndex(column => column.Name == _column);
        int candidate = table.Columns.ToList().FindIndex(column => column.Name == location.Column);
        return candidate.CompareTo(selected);
    }

    private void UpdateFindSelection()
    {
        if (_findResult is null) return;
        _findMatchIndex = _findResult.Matches.ToList().FindIndex(match => match.Location.RowNumber == _row && match.Location.Column == _column);
        UpdateFindStatus();
    }

    private void UpdateFindStatus()
    {
        if (_findResult is not { } result) return;
        string count = _findMatchIndex >= 0 ? $"Match {_findMatchIndex + 1:n0} of {result.Matches.Count:n0} matching cells"
            : $"{result.Matches.Count:n0} matching cells";
        string coverage = result.IsComplete ? (_findOptions?.Column is null ? "Selected table checked" : "Selected column checked") : "Partial search: " + (result.LimitReason ?? "scan limit reached");
        _findStatus.Text = $"{count} · {coverage}. {result.RowsScanned:n0} rows / {result.CellsScanned:n0} cells checked." +
            (_findMatchIndex >= 0 && result.Matches[_findMatchIndex].HighlightsTruncated ? " Some occurrences in this cell are not highlighted." : "");
    }

    private void UpdateFindControls()
    {
        bool table = !_disposed && _session is not null && _tables.SelectedItem is DatabaseTable;
        bool pageReady = PreviewState is "Ready" or "Empty";
        _grid.IsEnabled = table && pageReady;
        if (_findButton is null) return;
        _findOptionsButton.Content = new TextBlock
        {
            Text = $"{(_findMode.SelectedItem as DatabaseFindModeOption)?.Label ?? "All terms"} · {(_findColumns.SelectedItem as DatabaseFindColumnOption)?.Label ?? "All columns"}{(_findCase.IsChecked == true ? " · Aa" : "")} ▾",
            MaxWidth = 230,
            TextTrimming = TextTrimming.CharacterEllipsis,
        };
        _findButton.IsEnabled = table && pageReady && !_finding;
        _findStop.IsEnabled = table && _finding;
        _findPrevious.IsEnabled = _findNext.IsEnabled = table && pageReady && !_finding && FindMatchCount > 0;
        _go.IsEnabled = _rowAddress.IsEnabled = table && pageReady && !_finding;
        _previous.IsEnabled = table && pageReady && !_finding && _pageStart > 1;
        _next.IsEnabled = table && pageReady && !_finding && _pageHasMore && _pageStart + PageSize <= DatabaseFiles.MaxRows;
    }

    private DataGridTemplateColumn CreateValueColumn(string field, string column)
    {
        var text = new FrameworkElementFactory(typeof(TextBlock));
        text.SetValue(TextBlock.TextTrimmingProperty, TextTrimming.CharacterEllipsis);
        text.SetValue(FrameworkElement.MarginProperty, new Thickness(3, 1, 3, 1));
        text.SetBinding(HitHighlight.TextProperty, new Binding("[" + field + "]"));
        text.SetBinding(FrameworkElement.ToolTipProperty, new Binding("[" + field + "]"));
        var highlight = new MultiBinding { Converter = new FindCellConverter(this, column) };
        highlight.Bindings.Add(new Binding("[" + field + "]"));
        highlight.Bindings.Add(new Binding("[r]"));
        highlight.Bindings.Add(new Binding(nameof(FindHighlightVersion)) { Source = this });
        text.SetBinding(HitHighlight.HitProperty, highlight);
        return new DataGridTemplateColumn { Header = column, SortMemberPath = field, ClipboardContentBinding = new Binding("[" + field + "]"), CellTemplate = new DataTemplate { VisualTree = text } };
    }

    private sealed class FindCellConverter(DatabasePreviewControl owner, string column) : IMultiValueConverter
    {
        public object? Convert(object[] values, Type targetType, object parameter, CultureInfo culture)
        {
            if (values.Length < 2 || values[0] is not string text || values[1] is not long row ||
                !owner._findHighlights.TryGetValue((row, column), out var match) || match.Highlights.Count == 0) return null;
            int start = match.Highlights[0].Start;
            // Reused row bindings can briefly deliver the new value before its
            // row number. Wait for a compatible value rather than slicing it
            // with the previous row's distant highlight offsets.
            if ((uint)start >= (uint)text.Length) return null;
            int end = Math.Min(text.Length, Math.Max(start + 220, match.Highlights[0].Start + match.Highlights[0].Length));
            if (start > 0 && start < text.Length && char.IsLowSurrogate(text[start]) && char.IsHighSurrogate(text[start - 1])) start--;
            if (end < text.Length && end > 0 && char.IsHighSurrogate(text[end - 1]) && char.IsLowSurrogate(text[end])) end--;
            string prefix = start > 0 ? "…" : "";
            string excerpt = prefix + text[start..end] + (end < text.Length ? "…" : "");
            var spans = match.Highlights.Where(span => span.Start < end && span.Start + span.Length > start)
                .Select(span => new MatchSpan(Math.Max(span.Start, start) - start + prefix.Length,
                    Math.Min(span.Start + span.Length, end) - Math.Max(span.Start, start))).ToArray();
            return new Hit(string.Empty, checked((int)row), excerpt, spans);
        }
        public object[] ConvertBack(object value, Type[] targetTypes, object parameter, CultureInfo culture) => throw new NotSupportedException();
    }

    internal DatabasePreviewFindState? CaptureFindState() => _session is not null && _tables.SelectedItem is DatabaseTable table && _findOptions is not null && _findResult is not null
        ? new(table.Name, _session.Fingerprint, _findOptions, _findResult, _findPanel.Visibility == Visibility.Visible, _findOptionsPanel.Visibility == Visibility.Visible) : null;

    internal async Task RestoreFindStateAsync(DatabasePreviewFindState state)
    {
        if (_session?.Fingerprint != state.Fingerprint || (_tables.SelectedItem as DatabaseTable)?.Name != state.Table) return;
        _updatingFind = true;
        _findQuery.Text = state.Options.Query; _findMode.SelectedValue = state.Options.Mode;
        if (state.Options.Column is null) _findColumns.SelectedIndex = 0; else _findColumns.SelectedValue = state.Options.Column;
        _findCase.IsChecked = state.Options.MatchCase;
        _updatingFind = false;
        _findOptions = state.Options; _findResult = state.Result;
        foreach (var match in state.Result.Matches) _findHighlights[(match.Location.RowNumber, match.Location.Column)] = match;
        SetValue(FindHighlightVersionProperty, FindHighlightVersion + 1);
        SetFindVisible(state.IsOpen);
        _findOptionsPanel.Visibility = state.OptionsOpen ? Visibility.Visible : Visibility.Collapsed;
        FindState = !state.Result.IsComplete ? "Partial" : state.Result.Matches.Count == 0 ? "NoMatches" : "Ready";
        UpdateFindSelection(); UpdateFindControls();
        await KeepCurrentCellVisibleAsync();
    }

    private async Task KeepCurrentCellVisibleAsync()
    {
        if (_disposed) return;
        var measured = PagePhaseMeasured;
        measured?.Invoke("keep.scheduled", 0);
        long totalStarted = StartPreviewPhase(measured);
        long dispatchStarted = StartPreviewPhase(measured);
        int generation = _generation;
        var token = _lifetime.Token;
        try
        {
            await _grid.Dispatcher.InvokeAsync(() =>
            {
                FinishPreviewPhase(measured, "keep.dispatch-wait", dispatchStarted);
                if (_disposed || generation != _generation || !_grid.IsLoaded || _grid.CurrentCell.Item is not DataRowView || _grid.CurrentCell.Column is not { } column) return;
                long phaseStarted = StartPreviewPhase(measured);
                _grid.UpdateLayout();
                FinishPreviewPhase(measured, "keep.layout-before", phaseStarted);
                phaseStarted = StartPreviewPhase(measured);
                _grid.ScrollIntoView(_grid.CurrentCell.Item, column);
                FinishPreviewPhase(measured, "keep.scroll", phaseStarted);
                phaseStarted = StartPreviewPhase(measured);
                _grid.UpdateLayout();
                FinishPreviewPhase(measured, "keep.layout-after", phaseStarted);
                phaseStarted = StartPreviewPhase(measured);
                if (FindScrollViewer(_grid) is { } scroll)
                {
                    double left = _grid.Columns.Where(item => item.DisplayIndex < column.DisplayIndex).Sum(item => item.ActualWidth);
                    scroll.ScrollToHorizontalOffset(Math.Clamp(left - _grid.Columns[0].ActualWidth, 0, scroll.ScrollableWidth));
                }
                FinishPreviewPhase(measured, "keep.horizontal-scroll", phaseStarted);
            }, System.Windows.Threading.DispatcherPriority.ContextIdle, token);
        }
        catch (OperationCanceledException) { }
        finally { FinishPreviewPhase(measured, "keep.total", totalStarted); }
    }
}
