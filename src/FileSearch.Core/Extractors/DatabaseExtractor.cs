using System.Globalization;
using System.Runtime.CompilerServices;
using FileSearch.Core.Databases;
using FileSearch.Core.Indexing;

namespace FileSearch.Core.Extractors;

/// <summary>Searches scalar values in ordinary SQL tables; never runs caller SQL or database code.</summary>
public sealed class DatabaseExtractor(FileIndexOptions? indexOptions = null) : IDiagnosticTextExtractor
{
    public string ExtractorId => "filesearch.database";
    public string ExtractorVersion => "1";
    public IReadOnlyCollection<string> SupportedExtensions { get; } = DatabaseFiles.Extensions.ToArray();
    internal bool IsExcludedSource(string path) => string.Equals(Path.GetFullPath(path),
        Path.GetFullPath((indexOptions ?? new FileIndexOptions()).DatabasePath), StringComparison.OrdinalIgnoreCase);

    public IAsyncEnumerable<TextLine> ExtractAsync(string path, CancellationToken cancellationToken) =>
        ExtractAsync(path, NullExtractionIssueSink.Instance, cancellationToken);

    public async IAsyncEnumerable<TextLine> ExtractAsync(string path, IExtractionIssueSink issues,
        [EnumeratorCancellation] CancellationToken cancellationToken)
    {
        if (IsExcludedSource(path)) yield break;
        await using var session = await DatabaseReadSession.OpenAsync(path, null, cancellationToken).ConfigureAwait(false);
        int lineNumber = 0, cells = 0, characters = 0, rowsRead = 0;
        bool valueWarning = false;
        foreach (var table in session.Tables)
        {
            long first = 1;
            while (true)
            {
                var page = await session.ReadPageAsync(table.Name, first, 500, cancellationToken).ConfigureAwait(false);
                if (page.ValuesTruncated && !valueWarning)
                {
                    issues.Report(new ExtractionIssue(table.Name, "database_value_truncated", "Database cell text was limited to 16,384 characters; some text was not searched."));
                    valueWarning = true;
                }
                foreach (var row in page.Rows)
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    if (++rowsRead > DatabaseFiles.MaxRows) { ReportLimit(issues, table.Name); yield break; }
                    for (int column = 0; column < table.Columns.Count; column++)
                    {
                        string? value = row.Values[column];
                        if (string.IsNullOrWhiteSpace(value)) continue;
                        if (++cells > DatabaseFiles.MaxCells || (characters += value.Length) > DatabaseFiles.MaxSearchCharacters) { ReportLimit(issues, table.Name); yield break; }
                        var location = new DatabaseLocation(session.Kind, table.Name, row.Number, table.Columns[column].Name, row.Key, session.Fingerprint);
                        string label = string.Create(CultureInfo.InvariantCulture, $"{session.Kind} · {table.Name} · row {row.Number} · {location.Column}");
                        yield return new TextLine(++lineNumber, value, new SourceAnchor(SourceAnchorKind.Database, label, Column: column + 1, Database: location));
                    }
                }
                if (!page.HasMore) break;
                first += page.Rows.Count;
                if (first > DatabaseFiles.MaxRows) { ReportLimit(issues, table.Name); yield break; }
            }
        }
    }
    private static void ReportLimit(IExtractionIssueSink issues, string table) =>
        issues.Report(new ExtractionIssue(table, "database_search_limit", "Database search stopped at its limit of 100,000 rows, 250,000 populated scalar cells, or 16 million characters. Narrow the source database to search the remaining data."));
}
