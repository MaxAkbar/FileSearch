using System.Data;
using System.Windows;
using System.Windows.Controls;
using FileSearch.Gui.Services;
using ComboBox = System.Windows.Controls.ComboBox;
using Control = System.Windows.Controls.Control;
using DataGridCell = System.Windows.Controls.DataGridCell;
using Panel = System.Windows.Controls.Panel;

namespace FileSearch.Gui.Controls;

/// <summary>A paged, read-only grid retains sparse row/cell coordinates without realizing the entire workbook.</summary>
public sealed class SpreadsheetPreviewControl : System.Windows.Controls.DockPanel
{
    private readonly SpreadsheetPreview _workbook;
    private readonly ComboBox _sheets = new() { MinWidth = 90, MaxWidth = 220, DisplayMemberPath = nameof(PreviewSheet.Name) };
    private readonly System.Windows.Controls.TextBox _address = new() { Width = 72, Margin = new Thickness(6, 0, 0, 0) };
    private readonly TextBlock _status = new() { Margin = new Thickness(8, 4, 8, 4), TextWrapping = TextWrapping.Wrap };
    private readonly System.Windows.Controls.DataGrid _grid = new()
    {
        IsReadOnly = true,
        AutoGenerateColumns = true,
        CanUserAddRows = false,
        CanUserDeleteRows = false,
        EnableRowVirtualization = true,
        EnableColumnVirtualization = true,
        SelectionUnit = DataGridSelectionUnit.Cell,
        FrozenColumnCount = 1,
        MinColumnWidth = 60,
        MaxColumnWidth = 400,
    };
    private int _row = 1, _column = 1;
    private int _generation;
    public SpreadsheetPreview CurrentPosition => _workbook with { InitialSheet = ((PreviewSheet)_sheets.SelectedItem).Name, InitialRow = _row, InitialColumn = _column };
    private const int RowsPerPage = 250, ColumnsPerPage = 40;

    public SpreadsheetPreviewControl(SpreadsheetPreview workbook)
    {
        _workbook = workbook;
        foreach (var control in new System.Windows.Controls.Control[] { _sheets, _address, _grid })
        {
            control.SetResourceReference(Control.ForegroundProperty, "Atlas.InkBrush");
            control.SetResourceReference(BackgroundProperty, "Atlas.SurfaceBrush");
        }
        _sheets.Height = _address.Height = 28;
        _status.FontSize = 12;
        _status.SetResourceReference(TextBlock.ForegroundProperty, "Atlas.Ink2Brush");
        var rowStyle = new Style(typeof(DataGridRow));
        rowStyle.Setters.Add(new Setter(Control.ForegroundProperty, new DynamicResourceExtension("Atlas.InkBrush")));
        rowStyle.Setters.Add(new Setter(BackgroundProperty, new DynamicResourceExtension("Atlas.PaperBrush")));
        _grid.RowStyle = rowStyle;
        var headerStyle = new Style(typeof(System.Windows.Controls.Primitives.DataGridColumnHeader));
        headerStyle.Setters.Add(new Setter(Control.ForegroundProperty, new DynamicResourceExtension("Atlas.InkBrush")));
        headerStyle.Setters.Add(new Setter(BackgroundProperty, new DynamicResourceExtension("Atlas.SurfaceBrush")));
        _grid.ColumnHeaderStyle = headerStyle;
        var cellStyle = new Style(typeof(DataGridCell));
        cellStyle.Setters.Add(new Setter(Control.ForegroundProperty, new DynamicResourceExtension("Atlas.InkBrush")));
        var selection = new Trigger { Property = DataGridCell.IsSelectedProperty, Value = true };
        selection.Setters.Add(new Setter(BackgroundProperty, new DynamicResourceExtension("Atlas.SelBrush")));
        selection.Setters.Add(new Setter(Control.BorderBrushProperty, new DynamicResourceExtension("Atlas.AccentBrush")));
        cellStyle.Triggers.Add(selection);
        _grid.CellStyle = cellStyle;
        var toolbar = new WrapPanel { Margin = new Thickness(6) };
        toolbar.Children.Add(_sheets);
        toolbar.Children.Add(_address);
        AddButton(toolbar, "Go to cell", () => Go(_address.Text));
        AddButton(toolbar, "↑ Rows", () => Move(-RowsPerPage, 0));
        AddButton(toolbar, "↓ Rows", () => Move(RowsPerPage, 0));
        AddButton(toolbar, "← Columns", () => Move(0, -ColumnsPerPage));
        AddButton(toolbar, "→ Columns", () => Move(0, ColumnsPerPage));
        SetDock(toolbar, Dock.Top);
        SetDock(_status, Dock.Bottom);
        Children.Add(toolbar); Children.Add(_status); Children.Add(_grid);
        _sheets.ItemsSource = workbook.Sheets;
        _sheets.SelectedItem = workbook.Sheets.First(sheet => sheet.Name == workbook.InitialSheet);
        _sheets.SelectionChanged += (_, _) => { _row = _column = 1; ShowPage(); };
        _address.KeyDown += (_, args) => { if (args.Key == System.Windows.Input.Key.Enter) { Go(_address.Text); args.Handled = true; } };
        _row = workbook.InitialRow; _column = workbook.InitialColumn;
        System.Windows.Automation.AutomationProperties.SetName(_grid, "Read-only spreadsheet cells");
        System.Windows.Automation.AutomationProperties.SetName(_sheets, "Worksheet");
        System.Windows.Automation.AutomationProperties.SetName(_address, "Cell address");
        _grid.CurrentCellChanged += (_, _) =>
        {
            if (_grid.CurrentCell.Item is not DataRowView row || _grid.CurrentCell.Column?.Header is not string header ||
                !SpreadsheetPreviewService.TryCell(header + row[0], out int selectedRow, out int selectedColumn)) return;
            _row = selectedRow; _column = selectedColumn;
            _address.Text = header + row[0];
        };
        Loaded += (_, _) => ShowPage();
        ShowPage();
    }

    private void AddButton(Panel toolbar, string title, Action action)
    {
        var button = new System.Windows.Controls.Button { Content = title, Margin = new Thickness(3, 0, 0, 0), Padding = new Thickness(5, 3, 5, 3), MinWidth = 0, Height = 28 };
        button.SetResourceReference(StyleProperty, "SecondaryButtonStyle");
        button.Click += (_, _) => action();
        toolbar.Children.Add(button);
    }
    private void Go(string address)
    {
        if (!SpreadsheetPreviewService.TryCell(address, out int row, out int column)) { _status.Text = "Enter a cell such as A1 or D250."; return; }
        _row = row; _column = column; ShowPage();
    }
    private void Move(int rowDelta, int columnDelta)
    {
        var sheet = (PreviewSheet)_sheets.SelectedItem;
        _row = Math.Clamp(_row + rowDelta, 1, sheet.RowCount);
        _column = Math.Clamp(_column + columnDelta, 1, sheet.ColumnCount);
        ShowPage();
    }
    private void ShowPage()
    {
        int generation = ++_generation;
        if (_sheets.SelectedItem is not PreviewSheet sheet) return;
        _row = Math.Clamp(_row, 1, sheet.RowCount); _column = Math.Clamp(_column, 1, sheet.ColumnCount);
        int rowStart = ((_row - 1) / RowsPerPage) * RowsPerPage + 1;
        int columnStart = ((_column - 1) / ColumnsPerPage) * ColumnsPerPage + 1;
        int rowEnd = Math.Min(sheet.RowCount, rowStart + RowsPerPage - 1), columnEnd = Math.Min(sheet.ColumnCount, columnStart + ColumnsPerPage - 1);
        var table = new DataTable { Locale = System.Globalization.CultureInfo.InvariantCulture };
        table.Columns.Add("Row", typeof(int));
        for (int column = columnStart; column <= columnEnd; column++) table.Columns.Add(SpreadsheetPreviewService.ColumnName(column));
        for (int row = rowStart; row <= rowEnd; row++)
        {
            var values = new object[columnEnd - columnStart + 2]; values[0] = row;
            for (int column = columnStart; column <= columnEnd; column++) values[column - columnStart + 1] = sheet.Cell(row, column);
            table.Rows.Add(values);
        }
        _grid.ItemsSource = table.DefaultView;
        _address.Text = SpreadsheetPreviewService.ColumnName(_column) + _row;
        _status.Text = $"{sheet.Name} · Rows {rowStart:n0}–{rowEnd:n0} of {sheet.RowCount:n0} · Columns {SpreadsheetPreviewService.ColumnName(columnStart)}–{SpreadsheetPreviewService.ColumnName(columnEnd)} · Stored values; formulas are not recalculated.";
        _grid.Dispatcher.InvokeAsync(() =>
        {
            if (generation != _generation || !_grid.IsLoaded || _grid.Items.Count == 0 || _grid.Columns.Count < 2) return;
            _grid.UpdateLayout();
            int index = Math.Clamp(_row - rowStart, 0, _grid.Items.Count - 1);
            var column = _grid.Columns[Math.Clamp(_column - columnStart + 1, 1, _grid.Columns.Count - 1)];
            _grid.CurrentCell = new DataGridCellInfo(_grid.Items[index], column);
            _grid.SelectedCells.Clear(); _grid.SelectedCells.Add(_grid.CurrentCell);
            _grid.ScrollIntoView(_grid.Items[index], column);
            // DataGrid can realize the selected column before its scroll extent is established.
            // Apply its pixel offset after layout so a far-right match is visible on the first load.
            _grid.UpdateLayout();
            if (FindScrollViewer(_grid) is { } scroll)
            {
                double left = _grid.Columns.Where(item => item.DisplayIndex < column.DisplayIndex).Sum(item => item.ActualWidth);
                double frozen = _grid.Columns.OrderBy(item => item.DisplayIndex).Take(_grid.FrozenColumnCount).Sum(item => item.ActualWidth);
                scroll.ScrollToHorizontalOffset(Math.Clamp(left - frozen, 0, scroll.ScrollableWidth));
            }
        }, System.Windows.Threading.DispatcherPriority.ContextIdle);
    }

    private static ScrollViewer? FindScrollViewer(DependencyObject parent)
    {
        for (int index = 0; index < System.Windows.Media.VisualTreeHelper.GetChildrenCount(parent); index++)
        {
            var child = System.Windows.Media.VisualTreeHelper.GetChild(parent, index);
            if (child is ScrollViewer scroll) return scroll;
            if (FindScrollViewer(child) is { } descendant) return descendant;
        }
        return null;
    }
}
