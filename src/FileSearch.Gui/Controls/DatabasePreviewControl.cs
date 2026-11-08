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
    private readonly TextBox _rowAddress = new() { Name = "DatabaseRowAddress", Width = 75, Height = 28, Margin = new Thickness(4, 0, 0, 0) };
    private readonly TextBlock _status = new() { Name = "DatabaseLoadingStatus", Margin = new Thickness(6, 4, 6, 4), FontSize = 12, TextWrapping = TextWrapping.Wrap };
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
    private readonly Button _previous, _next, _go, _stopLoading;
    private readonly CancellationTokenSource _lifetime = new();
    private CancellationTokenSource? _pageLoad;
    private CancellationTokenSource? _openingLoad;
    private DatabaseReadSession? _session;
    private string? _sourcePath;
    private bool _loadingStopped;
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
        _stopLoading = AddButton(toolbar, "Stop loading", CancelLoading);
        _stopLoading.Name = "DatabaseStopLoadingButton";
        _stopLoading.Visibility = Visibility.Collapsed;
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
        System.Windows.Automation.AutomationProperties.SetName(_stopLoading, "Stop loading database preview");
        System.Windows.Automation.AutomationProperties.SetLiveSetting(_status, System.Windows.Automation.AutomationLiveSetting.Polite);
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
        string? previousPath = _sourcePath;
        string fullPath = System.IO.Path.GetFullPath(path);
        _sourcePath = fullPath;
        bool preserveGrid = previous is not null && string.Equals(previousPath, fullPath, StringComparison.OrdinalIgnoreCase) &&
            (location is null || location.Fingerprint == previous.Fingerprint);
        _settingTable = true; _tables.ItemsSource = null; _settingTable = false;
        if (!preserveGrid) _grid.ItemsSource = null;
        _grid.IsEnabled = false;
        PreviewState = "Loading";
        _loadingStopped = false;
        _stopLoading.Visibility = Visibility.Visible;
        UpdateFindControls();
        _status.Text = "Reading a private database snapshot…";
        int? initialPageGeneration = null;
        try
        {
            DatabaseReadSession session;
            bool reuse = false;
            if (previous is not null)
            {
                try
                {
                    reuse = await Task.Run(() => string.Equals(previousPath, fullPath, StringComparison.OrdinalIgnoreCase) &&
                        (location is null || location.Fingerprint == previous.Fingerprint) &&
                        DatabaseFiles.Fingerprint(fullPath) == previous.Fingerprint, openingToken);
                }
                catch
                {
                    var failed = previous; previous = null;
                    await CloseAsync(failed); throw;
                }
                if (!reuse)
                {
                    if (source == _sourceGeneration && !_disposed) _grid.ItemsSource = null;
                    var expired = previous; previous = null; await CloseAsync(expired);
                }
            }
            openingToken.ThrowIfCancellationRequested();
            var progress = new Progress<DatabasePreparationProgress>(value =>
            {
                if (_disposed || source != _sourceGeneration || openingToken.IsCancellationRequested || !ReferenceEquals(_openingLoad, opening)) return;
                _status.Text = value.Stage switch
                {
                    DatabasePreparationStage.Snapshot => $"Copying a private snapshot · {value.BytesCopied / 1048576d:n1} / {value.TotalBytes / 1048576d:n1} MB…",
                    DatabasePreparationStage.Engine => "Opening the private database snapshot…",
                    DatabasePreparationStage.Schema => "Reading database tables and columns…",
                    _ => "Checking that the source database is unchanged…",
                };
            });
            session = reuse ? previous! : await Task.Run(() => DatabaseReadSession.OpenAsync(fullPath, location?.Fingerprint, progress, openingToken), openingToken);
            previous = null;
            if (openingToken.IsCancellationRequested || source != _sourceGeneration || _disposed)
            { await session.DisposeAsync(); openingToken.ThrowIfCancellationRequested(); return; }
            _session = session;
            _settingTable = true;
            _tables.ItemsSource = session.Tables;
            _tables.SelectedItem = session.Tables.FirstOrDefault(table => table.Name == location?.Table) ?? (session.Tables.Count == 0 ? null : session.Tables[0]);
            _settingTable = false;
            _column = location?.Column ?? "";
            _rowKey = location?.RowKey ?? "";
            long requestedRow = location?.RowNumber ?? 1;
            _row = requestedRow;
            SetFindColumns();
            if (session.Tables.Count == 0) { PreviewState = "Empty"; _status.Text = "This database has no supported ordinary SQL tables. Views, virtual tables, and internal tables are excluded."; UpdateFindControls(); return; }
            var initialPage = LoadPageAsync(requestedRow, openingToken);
            initialPageGeneration = _generation;
            bool pageLoaded = await initialPage;
            openingToken.ThrowIfCancellationRequested();
            if (source == _sourceGeneration && initialPageGeneration == _generation && !_disposed &&
                (!pageLoaded || PreviewState is not ("Ready" or "Empty")))
            {
                _grid.ItemsSource = null;
                var failed = _session; _session = null;
                if (failed is not null) await CloseAsync(failed);
                if (source != _sourceGeneration || _disposed) return;
                PreviewState = "Unavailable";
                _status.Text = "The requested database record could not be loaded. Select the file again to retry.";
                UpdateFindControls();
            }
        }
        catch (OperationCanceledException) when (source == _sourceGeneration && !_disposed)
        {
            if (initialPageGeneration is { } pageGeneration && pageGeneration != _generation) return;
            _grid.ItemsSource = null;
            var canceled = _session; _session = null;
            if (canceled is not null) await CloseAsync(canceled);
            if (source != _sourceGeneration || _disposed) return;
            PreviewState = "Canceled";
            _status.Text = _loadingStopped ? "Database preview canceled. Select the file again to retry." : "Database preparation canceled or timed out. Select the file again or use a stable backup.";
            UpdateFindControls();
        }
        catch
        {
            if (source == _sourceGeneration && !_disposed && (initialPageGeneration is null || initialPageGeneration == _generation)) _grid.ItemsSource = null;
            throw;
        }
        finally
        {
            if (previous is not null) await CloseAsync(previous);
            if (ReferenceEquals(_openingLoad, opening))
            { _openingLoad = null; _stopLoading.Visibility = _pageLoad is null ? Visibility.Collapsed : Visibility.Visible; }
        }
    }

    public void CancelLoading()
    {
        if (_disposed) return;
        _loadingStopped = true;
        _openingLoad?.Cancel();
        _pageLoad?.Cancel();
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
    private async Task<bool> LoadPageAsync(long row, CancellationToken token, string? selectedColumn = null)
    {
        if (_disposed || _session is not { } session || _tables.SelectedItem is not DatabaseTable table) return false;
        var measured = PagePhaseMeasured;
        long totalStarted = StartPreviewPhase(measured);
        long phaseStarted = StartPreviewPhase(measured);
        string columnToSelect = selectedColumn ?? _column;
        _pageLoad?.Cancel(); _pageLoad?.Dispose();
        _pageLoad = CancellationTokenSource.CreateLinkedTokenSource(token, _lifetime.Token);
        var load = _pageLoad;
        var loadToken = load.Token;
        load.CancelAfter(TimeSpan.FromSeconds(15));
        int generation = ++_generation;
        PreviewState = "Loading";
        _loadingStopped = false;
        _stopLoading.Visibility = Visibility.Visible;
        _status.Text = "Loading records…";
        _previous.IsEnabled = _next.IsEnabled = false;
        UpdateFindControls();
        long first = ((Math.Clamp(row, 1, DatabaseFiles.MaxRows) - 1) / PageSize) * PageSize + 1;
        FinishPreviewPhase(measured, "page.ui-setup", phaseStarted);
        try
        {
            var (page, data, backgroundFinished) = await Task.Run(async () =>
            {
                long queryStarted = StartPreviewPhase(measured);
                var page = await session.ReadPageAsync(table.Name, first, PageSize, loadToken).ConfigureAwait(false);
                FinishPreviewPhase(measured, "page.query", queryStarted);
                long mappingStarted = StartPreviewPhase(measured);
                var data = CreatePageData(table, page, loadToken);
                FinishPreviewPhase(measured, "page.mapping", mappingStarted);
                return (page, data, StartPreviewPhase(measured));
            }, loadToken);
            FinishPreviewPhase(measured, "page.ui-resume-wait", backgroundFinished);
            loadToken.ThrowIfCancellationRequested();
            if (generation != _generation || _disposed) return false;
            phaseStarted = StartPreviewPhase(measured);
            bool sameTable = _grid.Columns.Count == table.Columns.Count + 2 && ReferenceEquals(_grid.Tag, table);
            bool reuseRows = sameTable && _grid.Columns.All(column => column.SortDirection is null);
            if (!sameTable)
            {
                _grid.Columns.Clear();
                _grid.Columns.Add(new DataGridTextColumn { Header = "Row", Binding = new Binding("[r]"), SortMemberPath = "r" });
                _grid.Columns.Add(new DataGridTextColumn { Header = "Record key", Binding = new Binding("[key]"), SortMemberPath = "key", MaxWidth = 220 });
                for (int index = 0; index < table.Columns.Count; index++)
                    _grid.Columns.Add(CreateValueColumn("c" + index.ToString(CultureInfo.InvariantCulture), table.Columns[index].Name));
                _grid.Tag = table;
            }
            foreach (var column in _grid.Columns) column.SortDirection = null;
            FinishPreviewPhase(measured, "page.columns", phaseStarted);
            phaseStarted = StartPreviewPhase(measured);
            ApplyPageData(data, reuseRows, loadToken);
            FinishPreviewPhase(measured, "page.items-source", phaseStarted);
            if (generation != _generation || _disposed) return false;
            _pageStart = first; _row = row; _column = columnToSelect;
            _rowKey = page.Rows.FirstOrDefault(record => record.Number == row)?.Key ?? "";
            _pageHasMore = page.HasMore;
            phaseStarted = StartPreviewPhase(measured);
            _rowAddress.Text = row.ToString(CultureInfo.InvariantCulture);
            _status.Text = $"{session.Kind} · {table.Name} · Rows {first:n0}–{first + page.Rows.Count - 1:n0} · Read-only snapshot. Binary and NULL values are blank." +
                (page.ValuesTruncated ? " Cell text limited to 16,384 characters." : "") + (page.HasMore && first + PageSize > DatabaseFiles.MaxRows ? " Preview stops at 100,000 rows." : "");
            FinishPreviewPhase(measured, "page.status-controls", phaseStarted);
            if (page.Rows.Count == 0)
            {
                _status.Text = "No rows on this page. Choose a smaller row number or another table.";
                loadToken.ThrowIfCancellationRequested();
                PreviewState = "Empty";
                UpdateFindControls();
                return generation == _generation && !_disposed;
            }
            long dispatchStarted = StartPreviewPhase(measured);
            await _grid.Dispatcher.InvokeAsync(() =>
            {
                FinishPreviewPhase(measured, "page.dispatch-wait", dispatchStarted);
                if (generation != _generation || !_grid.IsLoaded || _grid.Items.Count == 0) return;
                long layoutStarted = StartPreviewPhase(measured);
                _grid.UpdateLayout();
                FinishPreviewPhase(measured, "page.layout-before", layoutStarted);
                long selectionStarted = StartPreviewPhase(measured);
                var item = _grid.Items.Cast<DataRowView>().FirstOrDefault(item => (long)item["r"] == row) ?? (DataRowView)_grid.Items[0];
                int columnIndex = table.Columns.ToList().FindIndex(column => column.Name == columnToSelect);
                var member = "c" + columnIndex.ToString(CultureInfo.InvariantCulture);
                var column = _grid.Columns.FirstOrDefault(column => column.SortMemberPath == member) ?? _grid.Columns[2];
                _grid.CurrentCell = new DataGridCellInfo(item, column);
                _grid.SelectedCells.Clear(); _grid.SelectedCells.Add(_grid.CurrentCell);
                _grid.ScrollIntoView(item, column);
                FinishPreviewPhase(measured, "page.selection-scroll", selectionStarted);
                layoutStarted = StartPreviewPhase(measured);
                _grid.UpdateLayout();
                FinishPreviewPhase(measured, "page.layout-after", layoutStarted);
                long horizontalStarted = StartPreviewPhase(measured);
                if (FindScrollViewer(_grid) is { } scroll)
                {
                    double left = _grid.Columns.Where(item => item.DisplayIndex < column.DisplayIndex).Sum(item => item.ActualWidth);
                    scroll.ScrollToHorizontalOffset(Math.Clamp(left - _grid.Columns[0].ActualWidth, 0, scroll.ScrollableWidth));
                }
                FinishPreviewPhase(measured, "page.horizontal-scroll", horizontalStarted);
            }, System.Windows.Threading.DispatcherPriority.ContextIdle, loadToken);
            loadToken.ThrowIfCancellationRequested();
            if (generation != _generation || _disposed) return false;
            // The page is usable only after its requested record/cell has been
            // selected. Publishing Ready earlier exposes the previous current
            // cell while the dispatcher is still waiting to apply selection.
            PreviewState = "Ready";
            UpdateFindControls();
            return true;
        }
        catch (OperationCanceledException)
        {
            if (generation != _generation || _disposed) return false;
            PreviewState = _grid.Items.Count > 0 ? "Ready" : "Unavailable";
            _rowAddress.Text = _row.ToString(CultureInfo.InvariantCulture);
            _status.Text = _loadingStopped || token.IsCancellationRequested ? "Record navigation canceled." : "Database page loading timed out. Try a smaller row number or a stable backup.";
            UpdateFindControls();
            return false;
        }
        catch (Exception error)
        {
            if (generation != _generation || _disposed) return false;
            PreviewState = "Unavailable";
            ResetFind("Database records are unavailable. Reopen the preview or rerun the search.");
            FindState = "Unavailable";
            _status.Text = error is System.IO.InvalidDataException ? error.Message : "Database records could not be loaded. Close its application or use a stable backup.";
            UpdateFindControls();
            return false;
        }
        finally
        {
            if (ReferenceEquals(_pageLoad, load))
            {
                _pageLoad = null; load.Dispose();
                if (generation == _generation && _openingLoad is null) _stopLoading.Visibility = Visibility.Collapsed;
            }
            FinishPreviewPhase(measured, "page.total", totalStarted);
        }
    }

    private void ApplyPageData(DataTable prepared, bool canReuseRows, CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        if (canReuseRows && _grid.ItemsSource is DataView view && view.Table is { } current &&
            string.IsNullOrEmpty(view.Sort) && string.IsNullOrEmpty(view.RowFilter) && !view.ApplyDefaultSort &&
            view.RowStateFilter == DataViewRowState.CurrentRows && view.Count == current.Rows.Count && current.Rows.Count == prepared.Rows.Count &&
            _grid.Items.SortDescriptions.Count == 0 && _grid.Items.GroupDescriptions.Count == 0 &&
            current.Columns.Count == prepared.Columns.Count && current.Columns.Cast<DataColumn>().All(column =>
                column.ColumnName == prepared.Columns[column.Ordinal].ColumnName && column.DataType == prepared.Columns[column.Ordinal].DataType))
        {
            // Publish a complete bounded page in one dispatcher turn. Keeping the
            // existing row views lets realized cells update their bindings instead
            // of rebuilding the whole grid. Custom views keep replacement semantics.
            for (int index = 0; index < current.Rows.Count; index++) current.Rows[index].ItemArray = prepared.Rows[index].ItemArray;
            return;
        }
        _grid.ItemsSource = prepared.DefaultView;
    }

    private static DataTable CreatePageData(DatabaseTable table, DatabasePage page, CancellationToken token)
    {
        var data = new DataTable { Locale = CultureInfo.InvariantCulture };
        data.Columns.Add("r", typeof(long)); data.Columns.Add("key");
        for (int index = 0; index < table.Columns.Count; index++) data.Columns.Add("c" + index.ToString(CultureInfo.InvariantCulture));
        foreach (var record in page.Rows)
        {
            token.ThrowIfCancellationRequested();
            var values = new object[table.Columns.Count + 2]; values[0] = record.Number; values[1] = record.Key;
            for (int index = 0; index < record.Values.Count; index++) values[index + 2] = record.Values[index] ?? "";
            data.Rows.Add(values);
        }
        return data;
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
