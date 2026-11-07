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
public sealed class DatabasePreviewControl : DockPanel, IDisposable
{
    private readonly ComboBox _tables = new() { MinWidth = 100, MaxWidth = 260, Height = 28, DisplayMemberPath = nameof(DatabaseTable.Name) };
    private readonly TextBox _rowAddress = new() { Width = 75, Height = 28, Margin = new Thickness(4, 0, 0, 0) };
    private readonly TextBlock _status = new() { Margin = new Thickness(6, 4, 6, 4), FontSize = 12, TextWrapping = TextWrapping.Wrap };
    private readonly DataGrid _grid = new()
    {
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
    private readonly Button _previous, _next;
    private readonly CancellationTokenSource _lifetime = new();
    private CancellationTokenSource? _pageLoad;
    private DatabaseReadSession? _session;
    private long _row = 1, _pageStart = 1;
    private string _column = "";
    private int _generation;
    private bool _settingTable, _disposed;
    private const int PageSize = 250;
    public string PreviewState { get; private set; } = "Loading";
    public DatabaseLocation? CurrentLocation => _session is null || _tables.SelectedItem is not DatabaseTable table ? null :
        new DatabaseLocation(_session.Kind, table.Name, _row, _column, "", _session.Fingerprint);

    public DatabasePreviewControl()
    {
        var toolbar = new WrapPanel { Margin = new Thickness(6) };
        toolbar.Children.Add(_tables); toolbar.Children.Add(_rowAddress);
        AddButton(toolbar, "Go to row", () => Go());
        _previous = AddButton(toolbar, "‹ Rows", () => _ = LoadPageAsync(Math.Max(1, _pageStart - PageSize), _lifetime.Token));
        _next = AddButton(toolbar, "Rows ›", () => _ = LoadPageAsync(_pageStart + PageSize, _lifetime.Token));
        SetDock(toolbar, Dock.Top); SetDock(_status, Dock.Bottom);
        Children.Add(toolbar); Children.Add(_status); Children.Add(_grid);
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
        _tables.SelectionChanged += (_, _) => { if (!_settingTable) _ = LoadPageAsync(1, _lifetime.Token); };
        _rowAddress.KeyDown += (_, args) => { if (args.Key == System.Windows.Input.Key.Enter) { Go(); args.Handled = true; } };
        _grid.CurrentCellChanged += (_, _) =>
        {
            if (_grid.CurrentCell.Item is not DataRowView row || _grid.CurrentCell.Column?.SortMemberPath is not { } name || !name.StartsWith('c')) return;
            _row = (long)row["r"]; _column = _grid.CurrentCell.Column.Header?.ToString() ?? "";
            _rowAddress.Text = _row.ToString(CultureInfo.InvariantCulture);
        };
        System.Windows.Automation.AutomationProperties.SetName(_tables, "Database table");
        System.Windows.Automation.AutomationProperties.SetName(_rowAddress, "Database row number");
        System.Windows.Automation.AutomationProperties.SetName(_grid, "Read-only database records");
        Unloaded += (_, _) => Dispose();
    }

    public async Task InitializeAsync(string path, DatabaseLocation? location, CancellationToken token)
    {
        _status.Text = "Reading a private database snapshot…";
        using var opening = CancellationTokenSource.CreateLinkedTokenSource(token, _lifetime.Token);
        var session = await Task.Run(() => DatabaseReadSession.OpenAsync(path, location?.Fingerprint, opening.Token), opening.Token);
        if (opening.IsCancellationRequested) { await session.DisposeAsync(); opening.Token.ThrowIfCancellationRequested(); }
        _session = session;
        _settingTable = true;
        _tables.ItemsSource = session.Tables;
        _tables.SelectedItem = session.Tables.FirstOrDefault(table => table.Name == location?.Table) ?? (session.Tables.Count == 0 ? null : session.Tables[0]);
        _settingTable = false;
        _column = location?.Column ?? "";
        _row = location?.RowNumber ?? 1;
        if (session.Tables.Count == 0) { PreviewState = "Empty"; _status.Text = "This database has no supported ordinary SQL tables. Views, virtual tables, and internal tables are excluded."; return; }
        await LoadPageAsync(_row, opening.Token);
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
    private async Task LoadPageAsync(long row, CancellationToken token)
    {
        if (_disposed || _session is null || _tables.SelectedItem is not DatabaseTable table) return;
        _pageLoad?.Cancel(); _pageLoad?.Dispose();
        _pageLoad = CancellationTokenSource.CreateLinkedTokenSource(token, _lifetime.Token);
        var load = _pageLoad;
        load.CancelAfter(TimeSpan.FromSeconds(15));
        int generation = ++_generation;
        PreviewState = "Loading";
        _status.Text = "Loading records…";
        _grid.ItemsSource = null;
        _previous.IsEnabled = _next.IsEnabled = false;
        long first = ((Math.Clamp(row, 1, DatabaseFiles.MaxRows) - 1) / PageSize) * PageSize + 1;
        try
        {
            var page = await Task.Run(() => _session.ReadPageAsync(table.Name, first, PageSize, load.Token), load.Token);
            if (generation != _generation || load.IsCancellationRequested) return;
            _pageStart = first; _row = row;
            _grid.Columns.Clear();
            var data = new DataTable { Locale = CultureInfo.InvariantCulture };
            data.Columns.Add("r", typeof(long)); data.Columns.Add("key");
            _grid.Columns.Add(new DataGridTextColumn { Header = "Row", Binding = new Binding("[r]"), SortMemberPath = "r" });
            _grid.Columns.Add(new DataGridTextColumn { Header = "Record key", Binding = new Binding("[key]"), SortMemberPath = "key", MaxWidth = 220 });
            for (int index = 0; index < table.Columns.Count; index++)
            {
                string name = "c" + index.ToString(CultureInfo.InvariantCulture);
                data.Columns.Add(name);
                _grid.Columns.Add(new DataGridTextColumn { Header = table.Columns[index].Name, Binding = new Binding("[" + name + "]"), SortMemberPath = name });
            }
            foreach (var record in page.Rows)
            {
                var values = new object[table.Columns.Count + 2]; values[0] = record.Number; values[1] = record.Key;
                for (int index = 0; index < record.Values.Count; index++) values[index + 2] = record.Values[index] ?? "";
                data.Rows.Add(values);
            }
            _grid.ItemsSource = data.DefaultView;
            _rowAddress.Text = row.ToString(CultureInfo.InvariantCulture);
            _previous.IsEnabled = first > 1;
            _next.IsEnabled = page.HasMore && first + PageSize <= DatabaseFiles.MaxRows;
            _status.Text = $"{_session.Kind} · {table.Name} · Rows {first:n0}–{first + page.Rows.Count - 1:n0} · Read-only snapshot. Binary and NULL values are blank." +
                (page.ValuesTruncated ? " Cell text limited to 16,384 characters." : "") + (page.HasMore && first + PageSize > DatabaseFiles.MaxRows ? " Preview stops at 100,000 rows." : "");
            PreviewState = page.Rows.Count == 0 ? "Empty" : "Ready";
            if (page.Rows.Count == 0) { _status.Text = "No rows on this page. Choose a smaller row number or another table."; return; }
            _ = _grid.Dispatcher.InvokeAsync(() =>
            {
                if (generation != _generation || !_grid.IsLoaded || _grid.Items.Count == 0) return;
                _grid.UpdateLayout();
                int index = (int)Math.Clamp(row - first, 0, _grid.Items.Count - 1);
                var column = _grid.Columns.FirstOrDefault(column => column.Header?.ToString() == _column) ?? _grid.Columns[2];
                _grid.CurrentCell = new DataGridCellInfo(_grid.Items[index], column);
                _grid.SelectedCells.Clear(); _grid.SelectedCells.Add(_grid.CurrentCell);
                _grid.ScrollIntoView(_grid.Items[index], column);
                _grid.UpdateLayout();
                if (FindScrollViewer(_grid) is { } scroll)
                {
                    double left = _grid.Columns.Where(item => item.DisplayIndex < column.DisplayIndex).Sum(item => item.ActualWidth);
                    scroll.ScrollToHorizontalOffset(Math.Clamp(left - _grid.Columns[0].ActualWidth, 0, scroll.ScrollableWidth));
                }
            }, System.Windows.Threading.DispatcherPriority.ContextIdle, load.Token);
        }
        catch (OperationCanceledException) { if (generation == _generation && !_disposed) { PreviewState = "Unavailable"; _status.Text = "Database preview timed out. Close its application or use a smaller stable backup."; } }
        catch (Exception error)
        {
            if (generation != _generation || _disposed) return;
            PreviewState = "Unavailable";
            _status.Text = error is System.IO.InvalidDataException ? error.Message : "Database records could not be loaded. Close its application or use a stable backup.";
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
        _disposed = true; _generation++;
        _lifetime.Cancel(); _pageLoad?.Cancel();
        _pageLoad?.Dispose(); _lifetime.Dispose();
        var session = _session; _session = null;
        if (session is not null) _ = CloseAsync(session);
    }
    private static async Task CloseAsync(DatabaseReadSession session)
    {
        try { await session.DisposeAsync(); } catch (Exception) { /* Snapshot cleanup runs in the session's finally. */ }
    }
}
