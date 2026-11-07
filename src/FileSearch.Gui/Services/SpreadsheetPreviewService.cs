using System.Globalization;
using System.IO;
using System.Xml.Linq;
using FileSearch.Core.Engine;
using Microsoft.VisualBasic.FileIO;

namespace FileSearch.Gui.Services;

public sealed record PreviewSheet(string Name, IReadOnlyDictionary<int, IReadOnlyDictionary<int, string>> Rows,
    IReadOnlyDictionary<int, int> SourceLines, IReadOnlyDictionary<int, int>? SourceLineEnds = null)
{
    public int RowCount => Rows.Count == 0 ? 1 : Rows.Keys.Max();
    public int ColumnCount => Rows.Values.SelectMany(row => row.Keys).DefaultIfEmpty(1).Max();
    public string Cell(int row, int column) => Rows.TryGetValue(row, out var cells) && cells.TryGetValue(column, out var value) ? value : "";
    public int FindRow(int line) => SourceLines.FirstOrDefault(pair => line >= pair.Value &&
        line <= (SourceLineEnds?.GetValueOrDefault(pair.Key) ?? pair.Value)).Key;
}

public sealed record SpreadsheetPreview(IReadOnlyList<PreviewSheet> Sheets, string InitialSheet, int InitialRow = 1, int InitialColumn = 1);

public static class SpreadsheetPreviewService
{
    public const int MaxCells = 1_000_000;
    private static readonly XNamespace Spreadsheet = "http://schemas.openxmlformats.org/spreadsheetml/2006/main";
    private static readonly XNamespace Relationships = "http://schemas.openxmlformats.org/officeDocument/2006/relationships";

    public static Task<SpreadsheetPreview> LoadAsync(string path, Hit? hit, CancellationToken token) => Task.Run(() =>
    {
        var sheets = Path.GetExtension(path).Equals(".xlsx", StringComparison.OrdinalIgnoreCase) ? ReadXlsx(path, token) : ReadDelimited(path, token);
        return SelectTarget(sheets, hit);
    }, token);

    public static SpreadsheetPreview SelectTarget(IReadOnlyList<PreviewSheet> sheets, Hit? hit)
    {
        if (sheets.Count == 0) throw new InvalidDataException("The workbook contains no visible worksheets.");
        var locator = hit?.Snippet?.Locator ?? hit?.Locator;
        var name = locator?.Sheet ?? hit?.Anchor?.Sheet;
        if (name is null && hit?.LineContent.StartsWith('[') == true && hit.LineContent.IndexOf(']') is var close && close > 1)
            name = hit.LineContent[1..close];
        var sheet = sheets.FirstOrDefault(sheet => sheet.Name.Equals(name, StringComparison.OrdinalIgnoreCase)) ??
            sheets.FirstOrDefault(sheet => hit is not null && sheet.FindRow(hit.LineNumber) > 0) ?? sheets[0];
        string? matchingText = DocumentPreviewService.MatchedText(hit);
        if (name is null && matchingText is not null && sheets.All(item => item.SourceLines.Count == 0))
            sheet = sheets.FirstOrDefault(item => item.Rows.Values.Any(values => values.Values.Any(value => value.Contains(matchingText, StringComparison.OrdinalIgnoreCase)))) ?? sheet;
        var cell = locator?.CellRange ?? hit?.Anchor?.Cell;
        var target = TryCell(cell, out int row, out int column);
        if (!target)
        {
            row = hit is null ? 1 : sheet.FindRow(hit.LineNumber);
            row = Math.Max(1, row);
            column = 1;
            if (sheet.SourceLines.Count == 0 && matchingText is not null)
                row = sheet.Rows.FirstOrDefault(pair => pair.Value.Values.Any(value => value.Contains(matchingText, StringComparison.OrdinalIgnoreCase))).Key;
            row = Math.Max(1, row);
        }
        if (matchingText is not null && sheet.Rows.TryGetValue(row, out var matchingRow))
            column = matchingRow.FirstOrDefault(pair => pair.Value.Contains(matchingText, StringComparison.OrdinalIgnoreCase)).Key is > 0 and var matchedColumn ? matchedColumn : column;
        // Existing spreadsheet search lines join used cells with tabs. Preserve that mapping even with sparse columns.
        if (hit?.Highlights.Count > 0 && sheet.Rows.TryGetValue(row, out var values) && hit.LineContent.Contains('\t'))
        {
            int prefix = hit.LineContent.StartsWith('[') ? hit.LineContent.IndexOf("] ", StringComparison.Ordinal) + 2 : 0;
            var fields = hit.LineContent[Math.Max(0, prefix)..].Split('\t');
            var columns = values.Keys.Order().ToArray();
            int offset = Math.Max(0, prefix), position = hit.Highlights[0].Start;
            for (int i = 0; i < fields.Length && i < columns.Length; i++)
            {
                if (position >= offset && position < offset + fields[i].Length) { column = columns[i]; break; }
                offset += fields[i].Length + 1;
            }
        }
        return new SpreadsheetPreview(sheets, sheet.Name, Math.Clamp(row, 1, sheet.RowCount), Math.Clamp(column, 1, sheet.ColumnCount));
    }

    private static IReadOnlyList<PreviewSheet> ReadDelimited(string path, CancellationToken token)
    {
        if (new FileInfo(path).Length > PreviewPackage.MaxXmlBytes) throw new InvalidDataException("Delimited previews are limited to 32 MB.");
        using var parser = new TextFieldParser(path) { HasFieldsEnclosedInQuotes = true, TrimWhiteSpace = false };
        parser.SetDelimiters(Path.GetExtension(path).Equals(".tsv", StringComparison.OrdinalIgnoreCase) ? "\t" : ",");
        var rows = new Dictionary<int, IReadOnlyDictionary<int, string>>();
        var lines = new Dictionary<int, int>();
        var ends = new Dictionary<int, int>();
        int totalLines = File.ReadLines(path).Count();
        int cells = 0;
        while (!parser.EndOfData)
        {
            token.ThrowIfCancellationRequested();
            int sourceLine = checked((int)parser.LineNumber);
            var fields = parser.ReadFields() ?? [];
            if ((cells += fields.Length) > MaxCells || fields.Length > 16_384) throw new InvalidDataException("The table exceeds the one-million-cell preview limit.");
            int number = rows.Count + 1;
            rows.Add(number, fields.Select((value, index) => (value, index)).ToDictionary(item => item.index + 1, item => item.value));
            int endLine = parser.LineNumber < 0 ? totalLines : checked((int)parser.LineNumber) - 1;
            // A multiline quoted record maps every physical source line back to the same table row.
            lines.Add(number, sourceLine);
            ends.Add(number, endLine);
        }
        return [new PreviewSheet(Path.GetFileName(path), rows, lines, ends)];
    }

    private static List<PreviewSheet> ReadXlsx(string path, CancellationToken token)
    {
        using var archive = PreviewPackage.Open(path);
        var workbook = PreviewPackage.ReadXml(archive.GetEntry("xl/workbook.xml") ?? throw new InvalidDataException("Missing workbook."), token);
        bool date1904 = (string?)workbook.Root?.Element(Spreadsheet + "workbookPr")?.Attribute("date1904") is "1" or "true";
        var relations = PreviewPackage.ReadXml(archive.GetEntry("xl/_rels/workbook.xml.rels") ?? throw new InvalidDataException("Missing workbook relationships."), token)
            .Root!.Elements().Where(item => (string?)item.Attribute("TargetMode") != "External")
            .ToDictionary(item => (string)item.Attribute("Id")!, item => (string)item.Attribute("Target")!);
        var strings = archive.GetEntry("xl/sharedStrings.xml") is { } shared ? PreviewPackage.ReadXml(shared, token)
            .Descendants(Spreadsheet + "si").Select(item => string.Concat(item.Descendants(Spreadsheet + "t").Select(text => text.Value))).ToArray() : [];
        var dateStyles = new HashSet<int>();
        if (archive.GetEntry("xl/styles.xml") is { } styles)
        {
            var styleDoc = PreviewPackage.ReadXml(styles, token);
            var custom = styleDoc.Descendants(Spreadsheet + "numFmt").ToDictionary(item => (int)item.Attribute("numFmtId")!, item => (string?)item.Attribute("formatCode") ?? "");
            var formats = styleDoc.Root?.Element(Spreadsheet + "cellXfs")?.Elements(Spreadsheet + "xf").ToArray() ?? [];
            for (int i = 0; i < formats.Length; i++)
            {
                int id = (int?)formats[i].Attribute("numFmtId") ?? 0;
                if (id is >= 14 and <= 22 or >= 45 and <= 47 || custom.TryGetValue(id, out var format) && (format.Contains('y') || format.Contains('d'))) dateStyles.Add(i);
            }
        }
        var sheets = new List<PreviewSheet>();
        int count = 0, sourceLine = 0;
        foreach (var sheet in workbook.Descendants(Spreadsheet + "sheet"))
        {
            token.ThrowIfCancellationRequested();
            if (!relations.TryGetValue((string?)sheet.Attribute(Relationships + "id") ?? "", out var target)) continue;
            // OOXML permits package-absolute targets, which remain within this ZIP.
            var member = target.StartsWith('/') ? target.TrimStart('/') : PreviewPackage.ResolveMember("xl/workbook.xml", target);
            if (!PreviewPackage.IsSafeMember(member) || archive.GetEntry(member) is not { } entry) continue;
            var data = PreviewPackage.ReadXml(entry, token);
            var rows = new Dictionary<int, IReadOnlyDictionary<int, string>>();
            var lineMap = new Dictionary<int, int>();
            foreach (var row in data.Descendants(Spreadsheet + "row"))
            {
                token.ThrowIfCancellationRequested();
                int number = (int?)row.Attribute("r") ?? rows.Count + 1;
                if (number is < 1 or > 1_048_576) throw new InvalidDataException("Invalid worksheet row.");
                var values = new Dictionary<int, string>();
                foreach (var cell in row.Elements(Spreadsheet + "c"))
                {
                    if (++count > MaxCells) throw new InvalidDataException("The workbook exceeds the one-million-cell preview limit.");
                    if (!TryCell((string?)cell.Attribute("r"), out _, out int column)) column = values.Count + 1;
                    var value = cell.Element(Spreadsheet + "v")?.Value ?? "";
                    string type = (string?)cell.Attribute("t") ?? "";
                    if (type == "s" && int.TryParse(value, out int index) && index >= 0 && index < strings.Length) value = strings[index];
                    else if (type == "inlineStr") value = string.Concat(cell.Descendants(Spreadsheet + "t").Select(text => text.Value));
                    else if (type == "b") value = value == "1" ? "TRUE" : "FALSE";
                    else if (dateStyles.Contains((int?)cell.Attribute("s") ?? -1) && double.TryParse(value, CultureInfo.InvariantCulture, out double date) && date >= 0 && date < 2_958_466 - (date1904 ? 1462 : 0))
                        value = DateTime.FromOADate(date + (date1904 ? 1462 : 0)).ToString("yyyy-MM-dd HH:mm", CultureInfo.InvariantCulture);
                    if (value.Length == 0 && cell.Element(Spreadsheet + "f") is { } formula) value = "=" + formula.Value;
                    values[column] = value;
                }
                if (values.Count == 0) continue;
                rows.Add(number, values);
                lineMap.Add(number, ++sourceLine);
            }
            sheets.Add(new PreviewSheet((string?)sheet.Attribute("name") ?? "Sheet", rows, lineMap));
        }
        return sheets;
    }

    public static bool TryCell(string? address, out int row, out int column)
    {
        row = column = 0;
        if (string.IsNullOrWhiteSpace(address)) return false;
        var first = address.Split(':')[0].Replace("$", "", StringComparison.Ordinal);
        int index = 0;
        while (index < first.Length && char.IsAsciiLetter(first[index]))
        {
            if (column > 16_384) return false;
            column = column * 26 + char.ToUpperInvariant(first[index++]) - 'A' + 1;
        }
        return column is >= 1 and <= 16_384 && int.TryParse(first.AsSpan(index), out row) && row is >= 1 and <= 1_048_576;
    }

    public static string ColumnName(int column)
    {
        string name = "";
        while (column > 0) { column--; name = (char)('A' + column % 26) + name; column /= 26; }
        return name;
    }
}
