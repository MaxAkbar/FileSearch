using System.Data;
using System.Globalization;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using FileSearch.Core.Databases;
using Binding = System.Windows.Data.Binding;
using Button = System.Windows.Controls.Button;
using ComboBox = System.Windows.Controls.ComboBox;
using Control = System.Windows.Controls.Control;
using DataGrid = System.Windows.Controls.DataGrid;
using DataGridCell = System.Windows.Controls.DataGridCell;
using TextBox = System.Windows.Controls.TextBox;

namespace FileSearch.Gui.Controls;

/// <summary>Pages a private database snapshot and selects the source record identified by a search hit.</summary>
public sealed partial class DatabasePreviewControl : DockPanel, IDisposable
{
    private readonly ComboBox _tables = new() { Name = "DatabaseTableSelector", MinWidth = 100, MaxWidth = 260, Height = 28, DisplayMemberPath = nameof(DatabaseTable.Name) };
    private readonly TextBox _rowAddress = new() { Width = 75, Height = 28, Margin = new Thickness(4, 0, 0, 0) };
    private readonly TextBlock _status = new() { Margin = new Thickness(6, 4, 6, 4), FontSize = 12, TextWrapping = TextWrapping.Wrap };
    private readonly DataGrid _grid = new()
    {
        Name = "DatabaseRecordsGrid",
        IsReadOnly = true,
        AutoGenerateColumns = false,
        CanUserAddRows = false,
        CanUserDeleteRows = false,
        EnableRowVirtualization = true,
        EnableColumnVirtualization = true,
        SelectionUnit = DataGridSelectionUnit.Cell,
        FrozenColumnCount = 1,
        MinColumnWidth = 60,
        MaxColumnWidth = 400,
    };
    private readonly Button _previous, _next, _go;
    private readonly CancellationTokenSource _lifetime = new();
    private CancellationTokenSource? _pageLoad;
    private CancellationTokenSource? _openingLoad;
    private DatabaseReadSession? _session;
    private long _row = 1, _pageStart = 1;
    private string _column = "", _rowKey = "";
    private int _generation, _sourceGeneration;
    private bool _pageHasMore;
    private bool _settingTable, _disposed;
    private const int PageSize = 250;
    public string PreviewState { get; private set; } = "Loading";
    public DatabaseLocation? CurrentLocation => _session is null || _tables.SelectedItem is not DatabaseTable table ? null :
        new DatabaseLocation(_session.Kind, table.Name, _row, _column, _rowKey, _session.Fingerprint);

    public DatabasePreviewControl()
    {
        var toolbar = new WrapPanel { Margin = new Thickness(6) };
        toolbar.Children.Add(_tables); toolbar.Children.Add(_rowAddress);
        _go = AddButton(toolbar, "Go to row", () => Go());
        _previous = AddButton(toolbar, "‹ Rows", () => _ = LoadPageAsync(Math.Max(1, _pageStart - PageSize), _lifetime.Token));
        _next = AddButton(toolbar, "Rows ›", () => _ = LoadPageAsync(_pageStart + PageSize, _lifetime.Token));
        AddButton(toolbar, "Find in table", ToggleFind).Name = "DatabaseFindToggleButton";
        SetDock(toolbar, Dock.Top); SetDock(_status, Dock.Bottom);
        Children.Add(toolbar); Children.Add(CreateFindPanel()); Children.Add(_status); Children.Add(_grid);
        foreach (var control in new Control[] { _tables, _rowAddress, _grid })
        {
            control.SetResourceReference(Control.ForegroundProperty, "Atlas.InkBrush");
            control.SetResourceReference(Control.BackgroundProperty, "Atlas.SurfaceBrush");
        }
        _status.SetResourceReference(TextBlock.ForegroundProperty, "Atlas.Ink2Brush");
        var rowStyle = new Style(typeof(DataGridRow));
        rowStyle.Setters.Add(new Setter(Control.ForegroundProperty, new DynamicResourceExtension("Atlas.InkBrush")));
        rowStyle.Setters.Add(new Setter(Control.BackgroundProperty, new DynamicResourceExtension("Atlas.PaperBrush")));
        _grid.RowStyle = rowStyle;
        var headerStyle = new Style(typeof(System.Windows.Controls.Primitives.DataGridColumnHeader));
        headerStyle.Setters.Add(new Setter(Control.ForegroundProperty, new DynamicResourceExtension("Atlas.InkBrush")));
        headerStyle.Setters.Add(new Setter(Control.BackgroundProperty, new DynamicResourceExtension("Atlas.SurfaceBrush")));
        _grid.ColumnHeaderStyle = headerStyle;
        var cellStyle = new Style(typeof(DataGridCell));
        cellStyle.Setters.Add(new Setter(Control.ForegroundProperty, new DynamicResourceExtension("Atlas.InkBrush")));
        var selected = new Trigger { Property = DataGridCell.IsSelectedProperty, Value = true };
        selected.Setters.Add(new Setter(Control.BackgroundProperty, new DynamicResourceExtension("Atlas.SelBrush")));
        selected.Setters.Add(new Setter(Control.BorderBrushProperty, new DynamicResourceExtension("Atlas.AccentBrush")));
        cellStyle.Triggers.Add(selected); _grid.CellStyle = cellStyle;
        _tables.SelectionChanged += (_, _) =>
        {
            if (_settingTable) return;
            ResetFind("Table changed. Choose Find to search this table.");
            SetFindColumns();
            _column = _rowKey = "";
            _ = LoadPageAsync(1, _lifetime.Token);
        };
        _rowAddress.KeyDown += (_, args) => { if (args.Key == System.Windows.Input.Key.Enter) { Go(); args.Handled = true; } };
        _grid.CurrentCellChanged += (_, _) =>
        {
            if (_grid.CurrentCell.Item is not DataRowView row || _grid.CurrentCell.Column?.SortMemberPath is not { } name) return;
            _row = (long)row["r"]; _column = name.StartsWith('c') ? _grid.CurrentCell.Column.Header?.ToString() ?? "" : "";
            _rowKey = row["key"].ToString() ?? "";
            _rowAddress.Text = _row.ToString(CultureInfo.InvariantCulture);
            UpdateFindSelection();
        };
        System.Windows.Automation.AutomationProperties.SetName(_tables, "Database table");
        System.Windows.Automation.AutomationProperties.SetName(_rowAddress, "Database row number");
        System.Windows.Automation.AutomationProperties.SetName(_grid, "Read-only database records");
        SizeChanged += (_, _) => _ = KeepCurrentCellVisibleAsync();
        Unloaded += (_, _) => Dispose();
    }

    public async Task InitializeAsync(string path, DatabaseLocation? location, CancellationToken token)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        int source = ++_sourceGeneration;
        ++_generation;
        _pageLoad?.Cancel();
        ResetFind("Choose Find to search the selected table beyond this page.");
        _openingLoad?.Cancel(); _openingLoad?.Dispose();
        using var opening = CancellationTokenSource.CreateLinkedTokenSource(token, _lifetime.Token);
        _openingLoad = opening;
        var openingToken = opening.Token;
        var previous = _session; _session = null;
        _settingTable = true; _tables.ItemsSource = null; _settingTable = false;
        _grid.ItemsSource = null;
        PreviewState = "Loading";
        UpdateFindControls();
        _status.Text = "Reading a private database snapshot…";
        try
        {
            if (previous is not null) await CloseAsync(previous);
            openingToken.ThrowIfCancellationRequested();
            var session = await Task.Run(() => DatabaseReadSession.OpenAsync(path, location?.Fingerprint, openingToken), openingToken);
            if (openingToken.IsCancellationRequested || source != _sourceGeneration || _disposed)
            { await session.DisposeAsync(); openingToken.ThrowIfCancellationRequested(); return; }
            _session = session;
            _settingTable = true;
            _tables.ItemsSource = session.Tables;
            _tables.SelectedItem = session.Tables.FirstOrDefault(table => table.Name == location?.Table) ?? (session.Tables.Count == 0 ? null : session.Tables[0]);
            _settingTable = false;
            _column = location?.Column ?? "";
            _rowKey = location?.RowKey ?? "";
            _row = location?.RowNumber ?? 1;
            SetFindColumns();
            if (session.Tables.Count == 0) { PreviewState = "Empty"; _status.Text = "This database has no supported ordinary SQL tables. Views, virtual tables, and internal tables are excluded."; UpdateFindControls(); return; }
            await LoadPageAsync(_row, openingToken);
        }
        finally { if (ReferenceEquals(_openingLoad, opening)) _openingLoad = null; }
    }

    private static Button AddButton(System.Windows.Controls.Panel toolbar, string title, Action action)
    {
        var button = new Button { Content = title, Height = 28, MinWidth = 0, Padding = new Thickness(6, 2, 6, 2), Margin = new Thickness(4, 0, 0, 0) };
        button.SetResourceReference(StyleProperty, "SecondaryButtonStyle");
        button.Click += (_, _) => action(); toolbar.Children.Add(button); return button;
    }
    private void Go()
    {
        if (!long.TryParse(_rowAddress.Text, NumberStyles.None, CultureInfo.InvariantCulture, out long row) || row is < 1 or > DatabaseFiles.MaxRows)
        { _status.Text = "Enter a row number from 1 to 100,000."; return; }
        _ = LoadPageAsync(row, _lifetime.Token);
    }
    private async Task LoadPageAsync(long row, CancellationToken token, string? selectedColumn = null)
    {
        if (_disposed || _session is not { } session || _tables.SelectedItem is not DatabaseTable table) return;
        string columnToSelect = selectedColumn ?? _column;
        _pageLoad?.Cancel(); _pageLoad?.Dispose();
        _pageLoad = CancellationTokenSource.CreateLinkedTokenSource(token, _lifetime.Token);
        var load = _pageLoad;
        var loadToken = load.Token;
        load.CancelAfter(TimeSpan.FromSeconds(15));
        int generation = ++_generation;
        PreviewState = "Loading";
        _status.Text = "Loading records…";
        _previous.IsEnabled = _next.IsEnabled = false;
        UpdateFindControls();
        long first = ((Math.Clamp(row, 1, DatabaseFiles.MaxRows) - 1) / PageSize) * PageSize + 1;
        try
        {
            var page = await Task.Run(() => session.ReadPageAsync(table.Name, first, PageSize, loadToken), loadToken);
            loadToken.ThrowIfCancellationRequested();
            if (generation != _generation || _disposed) return;
            _pageStart = first; _row = row; _column = columnToSelect;
            _rowKey = page.Rows.FirstOrDefault(record => record.Number == row)?.Key ?? "";
            _pageHasMore = page.HasMore;
            _grid.Columns.Clear();
            var data = new DataTable { Locale = CultureInfo.InvariantCulture };
            data.Columns.Add("r", typeof(long)); data.Columns.Add("key");
            _grid.Columns.Add(new DataGridTextColumn { Header = "Row", Binding = new Binding("[r]"), SortMemberPath = "r" });
            _grid.Columns.Add(new DataGridTextColumn { Header = "Record key", Binding = new Binding("[key]"), SortMemberPath = "key", MaxWidth = 220 });
            for (int index = 0; index < table.Columns.Count; index++)
            {
                string name = "c" + index.ToString(CultureInfo.InvariantCulture);
                data.Columns.Add(name);
                _grid.Columns.Add(CreateValueColumn(name, table.Columns[index].Name));
            }
            foreach (var record in page.Rows)
            {
                var values = new object[table.Columns.Count + 2]; values[0] = record.Number; values[1] = record.Key;
                for (int index = 0; index < record.Values.Count; index++) values[index + 2] = record.Values[index] ?? "";
                data.Rows.Add(values);
            }
            _grid.ItemsSource = data.DefaultView;
            _rowAddress.Text = row.ToString(CultureInfo.InvariantCulture);
            _status.Text = $"{session.Kind} · {table.Name} · Rows {first:n0}–{first + page.Rows.Count - 1:n0} · Read-only snapshot. Binary and NULL values are blank." +
                (page.ValuesTruncated ? " Cell text limited to 16,384 characters." : "") + (page.HasMore && first + PageSize > DatabaseFiles.MaxRows ? " Preview stops at 100,000 rows." : "");
            PreviewState = page.Rows.Count == 0 ? "Empty" : "Ready";
            UpdateFindControls();
            if (page.Rows.Count == 0) { _status.Text = "No rows on this page. Choose a smaller row number or another table."; return; }
            await _grid.Dispatcher.InvokeAsync(() =>
            {
                if (generation != _generation || !_grid.IsLoaded || _grid.Items.Count == 0) return;
                _grid.UpdateLayout();
                var item = _grid.Items.Cast<DataRowView>().FirstOrDefault(item => (long)item["r"] == row) ?? (DataRowView)_grid.Items[0];
                int columnIndex = table.Columns.ToList().FindIndex(column => column.Name == columnToSelect);
                var member = "c" + columnIndex.ToString(CultureInfo.InvariantCulture);
                var column = _grid.Columns.FirstOrDefault(column => column.SortMemberPath == member) ?? _grid.Columns[2];
                _grid.CurrentCell = new DataGridCellInfo(item, column);
                _grid.SelectedCells.Clear(); _grid.SelectedCells.Add(_grid.CurrentCell);
                _grid.ScrollIntoView(item, column);
                _grid.UpdateLayout();
                if (FindScrollViewer(_grid) is { } scroll)
                {
                    double left = _grid.Columns.Where(item => item.DisplayIndex < column.DisplayIndex).Sum(item => item.ActualWidth);
                    scroll.ScrollToHorizontalOffset(Math.Clamp(left - _grid.Columns[0].ActualWidth, 0, scroll.ScrollableWidth));
                }
            }, System.Windows.Threading.DispatcherPriority.ContextIdle, loadToken);
        }
        catch (OperationCanceledException)
        {
            if (generation != _generation || _disposed) return;
            PreviewState = _grid.Items.Count > 0 ? "Ready" : "Unavailable";
            _rowAddress.Text = _row.ToString(CultureInfo.InvariantCulture);
            _status.Text = token.IsCancellationRequested ? "Record navigation canceled." : "Database page loading timed out. Try a smaller row number or a stable backup.";
            UpdateFindControls();
        }
        catch (Exception error)
        {
            if (generation != _generation || _disposed) return;
            PreviewState = "Unavailable";
            ResetFind("Database records are unavailable. Reopen the preview or rerun the search.");
            FindState = "Unavailable";
            _status.Text = error is System.IO.InvalidDataException ? error.Message : "Database records could not be loaded. Close its application or use a stable backup.";
            UpdateFindControls();
        }
    }
    private static ScrollViewer? FindScrollViewer(DependencyObject parent)
    {
        for (int index = 0; index < System.Windows.Media.VisualTreeHelper.GetChildrenCount(parent); index++)
        {
            var child = System.Windows.Media.VisualTreeHelper.GetChild(parent, index);
            if (child is ScrollViewer scroll) return scroll;
            if (FindScrollViewer(child) is { } result) return result;
        }
        return null;
    }
    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true; _generation++; _sourceGeneration++;
        CancelFindForLifetime();
        _lifetime.Cancel(); _pageLoad?.Cancel(); _openingLoad?.Cancel();
        _pageLoad?.Dispose(); _openingLoad?.Dispose(); _openingLoad = null; _lifetime.Dispose();
        var session = _session; _session = null;
        if (session is not null) _ = CloseAsync(session);
    }
    private static async Task CloseAsync(DatabaseReadSession session)
    {
        try { await session.DisposeAsync(); } catch (Exception) { /* Snapshot cleanup runs in the session's finally. */ }
    }
}
