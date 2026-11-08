using System.Globalization;
using System.IO.Compression;
using System.Xml;

namespace FileSearch.Core.Extractors;

public sealed partial class ZipExtractor
{
    private readonly Func<IExtractorRegistry> _documents;
    private readonly IOutOfProcessExtractionService? _documentHost;
    private readonly bool _allowDirectDocumentParsing;
    private readonly string? _temporaryDirectory;
    private static readonly Lazy<IExtractorRegistry> BuiltInDocuments = new(() =>
        new ExtractorRegistry([new PdfExtractor(), new WordExtractor(), new ExcelExtractor(), new PowerPointExtractor()]));

    private static bool IsDocumentEntry(string path) => Path.GetExtension(path).ToLowerInvariant() is
        ".pdf" or ".docx" or ".xlsx" or ".pptx";

    private static bool IsSafeMember(string member)
    {
        if (string.IsNullOrWhiteSpace(member) || member.Length > 512 || member[0] is '/' or '\\' ||
            member.Contains('\\') || member.Contains(':') || member.Any(char.IsControl))
            return false;
        return !member.Split('/').Any(part => part is "." or "..");
    }

    private static SourceAnchor ArchiveAnchor(string member, string fingerprint, SourceAnchor? inner) =>
        new(SourceAnchorKind.Archive,
            inner is null ? $"archive member {member}" : $"archive member {member} · {inner.DisplayText}",
            Line: inner?.Line, Column: inner?.Column, Page: inner?.Page, Section: inner?.Section,
            Sheet: inner?.Sheet, Cell: inner?.Cell, MemberPath: member,
            X: inner?.X, Y: inner?.Y, Width: inner?.Width, Height: inner?.Height,
            SourceWidth: inner?.SourceWidth, SourceHeight: inner?.SourceHeight,
            ArchiveFingerprint: fingerprint, ArchiveContentAnchor: inner);

    private static SourceAnchor DocumentAnchor(TextLine line, string name)
    {
        if (line.Anchor is not null) return line.Anchor.Line is null ? line.Anchor with { Line = line.Number } : line.Anchor;
        if (Path.GetExtension(name).Equals(".pptx", StringComparison.OrdinalIgnoreCase) &&
            line.Content.StartsWith("[Slide ", StringComparison.Ordinal) && line.Content.IndexOf(']') is var end && end > 7)
        {
            var label = line.Content[1..end];
            var number = label.AsSpan(6);
            int space = number.IndexOf(' ');
            if (space >= 0) number = number[..space];
            if (int.TryParse(number, NumberStyles.None, CultureInfo.InvariantCulture, out int slide) && slide > 0)
                return new SourceAnchor(SourceAnchorKind.PowerPoint, label, Line: line.Number, Page: slide, Section: label);
        }
        var kind = Path.GetExtension(name).Equals(".docx", StringComparison.OrdinalIgnoreCase)
            ? SourceAnchorKind.Word : SourceAnchorKind.Text;
        return new SourceAnchor(kind, $"line {line.Number}", Line: line.Number);
    }

    private async Task<IReadOnlyList<TextLine>> ReadDocumentAsync(ZipArchiveEntry entry, byte[] bytes,
        IOutOfProcessExtractionService? host, int availableCharacters, IExtractionIssueSink issues, CancellationToken token)
    {
        var reader = _documents().GetFor(entry.Name);
        if (reader is null || !reader.SupportedExtensions.Contains(Path.GetExtension(entry.Name), StringComparer.OrdinalIgnoreCase))
        {
            ReportIssue(issues, entry, "archive_document_reader_unavailable", "Archive document skipped because no registered reader supports its format.");
            return [];
        }
        bool canUseHost = host is IArchiveDocumentExtractionService boundedHost
            ? boundedHost.ShouldUseDocumentMember(reader) : host?.ShouldUse(reader) == true;
        if (!_allowDirectDocumentParsing && !canUseHost)
        {
            ReportIssue(issues, entry, "archive_document_host_unavailable", "Archive document skipped because the isolated extraction host is unavailable; coverage is partial.");
            return [];
        }

        string parent = _temporaryDirectory ?? Path.Combine(Path.GetTempPath(), "FileSearchArchiveMembers");
        string directory = Path.Combine(parent, Guid.NewGuid().ToString("N"));
        string path = Path.Combine(directory, "member" + Path.GetExtension(entry.Name).ToLowerInvariant());
        var lines = new List<TextLine>();
        int remainingCharacters = Math.Min(_policy.MaxEntryTextCharacters, availableCharacters);
        try
        {
            Directory.CreateDirectory(directory);
            await File.WriteAllBytesAsync(path, bytes, token).ConfigureAwait(false);
            token.ThrowIfCancellationRequested();
            if (_allowDirectDocumentParsing)
            {
                // Only the isolated ZIP host (or an explicit test hook) may
                // invoke synchronous document parsers directly. Its parent
                // owns the workspace and hard process deadline.
                await foreach (var line in reader.ExtractWithContextAsync(path, new TextExtractionContext(EnableOcr: false), token).ConfigureAwait(false))
                {
                    if (!Add(line)) break;
                }
            }
            else
            {
                var result = host is IArchiveDocumentExtractionService documentHost
                    ? await documentHost.ExtractDocumentMemberAsync(path, reader, token).ConfigureAwait(false)
                    : await host!.ExtractAsync(path, reader, token).ConfigureAwait(false);
                foreach (var issue in result.Issues)
                    ReportIssue(issues, entry, issue.Code, issue.Message);
                foreach (var line in result.Lines)
                {
                    token.ThrowIfCancellationRequested();
                    if (!Add(line)) break;
                }
            }
        }
        catch (ExtractorHostException error)
        {
            ReportIssue(issues, entry, error.Code == "extractor_host_timeout" ? "archive_document_timeout" : "archive_document_failed",
                $"Archive document skipped or partially read: {error.Message}");
        }
        catch (Exception error) when (error is not (OperationCanceledException or OutOfMemoryException))
        {
            ReportIssue(issues, entry, "archive_document_failed", $"Archive document skipped or partially read: {error.Message}");
        }
        finally
        {
            // Neither member names nor document contents form a filesystem
            // path. The isolated host's parent also removes its workspace
            // when a timeout requires killing the parser process.
            try { File.Delete(path); Directory.Delete(directory); }
            catch (IOException) { }
            catch (UnauthorizedAccessException) { }
        }
        return lines;

        bool Add(TextLine line)
        {
            if (lines.Count >= _policy.MaxEntryTextLines || remainingCharacters <= 0)
            {
                ReportIssue(issues, entry, "archive_member_text_limit", "Archive document text or line budget was reached; coverage is partial.");
                return false;
            }
            string text = line.Content;
            bool truncated = text.Length > remainingCharacters;
            if (truncated) text = text[..remainingCharacters];
            remainingCharacters -= text.Length;
            lines.Add(line with { Content = text });
            if (truncated)
                ReportIssue(issues, entry, "archive_member_text_limit", "Archive document text was truncated at its output budget; coverage is partial.");
            return !truncated;
        }
    }

    private sealed record PackageCheck(bool Allowed, long BytesRead, string Code = "", string Message = "");

    private async Task<PackageCheck> CheckDocumentPackageAsync(byte[] bytes, string name, long remainingBudget, CancellationToken token)
    {
        if (Path.GetExtension(name).Equals(".pdf", StringComparison.OrdinalIgnoreCase)) return new(true, 0);
        long consumed = 0;
        try
        {
            using var input = new MemoryStream(bytes, writable: false);
            using var package = new ZipArchive(input, ZipArchiveMode.Read);
            if (package.Entries.Count > _policy.MaxPackageEntries)
                return new(false, consumed, "archive_document_package_limit", "Office document skipped because its internal package entry limit was exceeded.");
            long budget = Math.Min(remainingBudget, _policy.MaxPackageBytes);
            var names = new HashSet<string>(StringComparer.Ordinal);
            foreach (var part in package.Entries)
            {
                token.ThrowIfCancellationRequested();
                if (!IsSafeMember(part.FullName) || !names.Add(part.FullName))
                    return new(false, consumed, "archive_document_package_unsafe", "Office document skipped because an internal package name is unsafe or duplicated.");
                if (part.Name.Length == 0) continue;
                long allowed = Math.Min(_policy.MaxEntryBytes, Math.Max(0, budget - consumed));
                if (part.Length > allowed)
                    return new(false, consumed, "archive_document_package_limit", "Office document skipped because its internal expanded byte budget was exceeded.");
                var read = await ReadBinaryEntryAsync(part, allowed, token).ConfigureAwait(false);
                consumed += read.BytesRead;
                if (read.Truncated)
                    return new(false, consumed, "archive_document_package_limit", "Office document skipped because an internal part exceeded its actual expanded byte budget.");
                if (!part.FullName.EndsWith(".xml", StringComparison.OrdinalIgnoreCase) &&
                    !part.FullName.EndsWith(".rels", StringComparison.OrdinalIgnoreCase)) continue;
                using var xmlInput = new MemoryStream(read.Bytes, writable: false);
                using var xml = XmlReader.Create(xmlInput, new XmlReaderSettings
                {
                    DtdProcessing = DtdProcessing.Prohibit,
                    XmlResolver = null,
                    MaxCharactersInDocument = _policy.MaxEntryBytes,
                });
                while (xml.Read())
                {
                    token.ThrowIfCancellationRequested();
                    if (xml.Depth > _policy.MaxXmlDepth)
                        return new(false, consumed, "archive_document_xml_depth", "Office document skipped because XML nesting exceeded its depth limit.");
                }
            }
            return new(true, consumed);
        }
        catch (Exception error) when (error is IOException or InvalidDataException or XmlException or NotSupportedException)
        {
            return new(false, consumed, "archive_document_package_unreadable", "Office document skipped because its package is protected, corrupt, or contains unsupported XML.");
        }
    }
}
