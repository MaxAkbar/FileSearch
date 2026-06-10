using System.Collections.Generic;
using System.Linq;
using System.Runtime.CompilerServices;
using System.Threading;
using System.Threading.Tasks;
using ClosedXML.Excel;

namespace FileSearch.Core.Extractors;

/// <summary>
/// Extracts cell text from .xlsx workbooks using ClosedXML.
/// Each non-empty row becomes one <see cref="TextLine"/>, prefixed with
/// "[Sheet] " for grep-ability, and cells joined with tabs.
/// </summary>
public sealed class ExcelExtractor : ITextExtractor
{
    public IReadOnlyCollection<string> SupportedExtensions { get; } = new[] { ".xlsx" };

    public async IAsyncEnumerable<TextLine> ExtractAsync(
        string path,
        [EnumeratorCancellation] CancellationToken cancellationToken)
    {
        // ClosedXML loads synchronously, so open on the thread pool. The
        // iterator still runs between yields on the consumer's thread — UI
        // callers must consume from a background task (FilePreviewService does).
        var workbook = await Task.Run(() => new XLWorkbook(path), cancellationToken).ConfigureAwait(false);
        using (workbook)
        {
            int lineNumber = 0;
            foreach (var sheet in workbook.Worksheets)
            {
                cancellationToken.ThrowIfCancellationRequested();
                string sheetName = sheet.Name;

                foreach (var row in sheet.RowsUsed())
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    var cells = row.CellsUsed().Select(c => c.GetString());
                    var joined = string.Join('\t', cells);
                    if (string.IsNullOrEmpty(joined)) continue;

                    lineNumber++;
                    yield return new TextLine(lineNumber, $"[{sheetName}] {joined}");
                }
            }
        }
    }
}
