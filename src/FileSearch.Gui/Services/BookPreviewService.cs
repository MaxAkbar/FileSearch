using System.Globalization;
using System.IO;
using System.IO.Compression;
using System.Text;
using System.Xml.Linq;
using FileSearch.Core.Engine;

namespace FileSearch.Gui.Services;

/// <summary>Reflowed book and OpenDocument contents. No Office automation or active content.</summary>
public static class BookPreviewService
{
    private static readonly string[] TextStyleProperties = ["font-weight", "font-style", "color", "font-size"];
    private static readonly XNamespace Text = "urn:oasis:names:tc:opendocument:xmlns:text:1.0";
    private static readonly XNamespace Table = "urn:oasis:names:tc:opendocument:xmlns:table:1.0";
    private static readonly XNamespace Draw = "urn:oasis:names:tc:opendocument:xmlns:drawing:1.0";
    private static readonly XNamespace XLink = "http://www.w3.org/1999/xlink";
    private static readonly XNamespace Style = "urn:oasis:names:tc:opendocument:xmlns:style:1.0";
    private static readonly XNamespace Fo = "urn:oasis:names:tc:opendocument:xmlns:xsl-fo-compatible:1.0";
    private static readonly XNamespace Office = "urn:oasis:names:tc:opendocument:xmlns:office:1.0";

    public static Task<DocumentPreview> LoadAsync(string path, Hit? hit, string? selectedPart, CancellationToken token) => Task.Run(() =>
    {
        var content = new HtmlPreviewContent();
        if (Path.GetExtension(path).Equals(".rtf", StringComparison.OrdinalIgnoreCase))
        {
            // RtfPipe recognizes the document's declared ANSI code page.
            Encoding.RegisterProvider(CodePagesEncodingProvider.Instance);
            if (new FileInfo(path).Length > DocumentPreviewService.MaxTextBytes) throw new InvalidDataException("RTF previews are limited to 4 MB.");
            using var stream = File.OpenRead(path);
            token.ThrowIfCancellationRequested();
            var html = RtfPipe.Rtf.ToHtml(stream);
            token.ThrowIfCancellationRequested();
            return content.Document("RTF · Formatted content", content.Sanitize(html));
        }
        using var archive = PreviewPackage.Open(path);
        if (Path.GetExtension(path).Equals(".epub", StringComparison.OrdinalIgnoreCase))
            return ReadEpub(archive, hit, selectedPart, content, token);
        var document = PreviewPackage.ReadXml(archive.GetEntry("content.xml") ?? throw new InvalidDataException("Missing OpenDocument content."), token);
        if (Path.GetExtension(path).Equals(".ods", StringComparison.OrdinalIgnoreCase))
        {
            var workbook = ReadOds(document, hit, token);
            return new DocumentPreview(DocumentPreviewKind.Spreadsheet, "ODS · Stored cell values", Workbook: workbook);
        }
        var styles = document.Descendants(Style + "style").Where(item => item.Attribute(Style + "name") is not null)
            .GroupBy(item => (string)item.Attribute(Style + "name")!).ToDictionary(group => group.Key, group => group.First().Element(Style + "text-properties"));
        var slides = document.Descendants(Draw + "page").ToArray();
        PreviewPart[]? parts = slides.Length == 0 ? null : slides.Select((slide, index) =>
            new PreviewPart((index + 1).ToString(CultureInfo.InvariantCulture), (string?)slide.Attribute(Draw + "name") ?? $"Slide {index + 1}")).ToArray();
        string? matchingText = DocumentPreviewService.MatchedText(hit);
        string? matchingSlide = matchingText is null ? null : slides.Select((slide, index) => (slide, index))
            .Where(item => item.slide.Value.Contains(matchingText, StringComparison.OrdinalIgnoreCase))
            .Select(item => (item.index + 1).ToString(CultureInfo.InvariantCulture)).FirstOrDefault();
        string? chosen = parts is null ? null : parts.FirstOrDefault(part => part.Id == selectedPart)?.Id ??
            matchingSlide ??
            parts.ElementAtOrDefault(Math.Max(0, (hit?.Snippet?.Locator?.Page ?? hit?.Locator?.Page ?? hit?.Anchor?.Page ?? 1) - 1))?.Id ?? parts[0].Id;
        var root = chosen is null ? document.Root! : slides[int.Parse(chosen, CultureInfo.InvariantCulture) - 1];
        string ConvertNode(XNode node)
        {
            token.ThrowIfCancellationRequested();
            if (node is XText text) return HtmlPreviewContent.Encode(text.Value);
            if (node is not XElement element) return "";
            if (element.Name == Draw + "image")
            {
                var resource = PackageImage(archive, "content.xml", (string?)element.Attribute(XLink + "href") ?? "", token);
                return resource is null ? "" : "<img alt='Document image' src='" + content.AddResource(resource) + "'>";
            }
            if (element.Name == Text + "s") return new string(' ', Math.Clamp((int?)element.Attribute(Text + "c") ?? 1, 1, 1000)).Replace(" ", "&nbsp;", StringComparison.Ordinal);
            if (element.Name == Text + "tab") return "&emsp;";
            if (element.Name == Text + "line-break") return "<br>";
            string inner = string.Concat(element.Nodes().Select(ConvertNode));
            string tag = element.Name == Text + "h" ? "h2" : element.Name == Text + "p" ? "p" :
                element.Name == Text + "list" ? "ul" : element.Name == Text + "list-item" ? "li" :
                element.Name == Table + "table" ? "table" : element.Name == Table + "table-row" ? "tr" :
                element.Name == Table + "table-cell" || element.Name == Table + "covered-table-cell" ? "td" : "span";
            string css = "";
            if (styles.TryGetValue((string?)element.Attribute(Text + "style-name") ?? "", out var properties) && properties is not null)
                foreach (string property in TextStyleProperties)
                    if (properties.Attribute(Fo + property) is { } value) css += property + ":" + value.Value + ";";
            return "<" + tag + (css.Length == 0 ? "" : " style='" + HtmlPreviewContent.Encode(css) + "'") + ">" + inner + "</" + tag + ">";
        }
        // This HTML is generated from XML names and encoded values. Sanitize CSS, then retain only our own image resources.
        var generated = string.Concat(root.Nodes().Select(ConvertNode));
        var safe = content.Sanitize(generated, source => content.Resources.GetValueOrDefault(source));
        return content.Document(slides.Length > 0 ? "ODP · Reflowed slide content" : "ODT · Reflowed document content", safe, parts, chosen);
    }, token);

    private static DocumentPreview ReadEpub(ZipArchive archive, Hit? hit, string? selected, HtmlPreviewContent content, CancellationToken token)
    {
        var container = PreviewPackage.ReadXml(archive.GetEntry("META-INF/container.xml") ?? throw new InvalidDataException("Missing EPUB container."), token);
        string packageName = (string?)container.Descendants().FirstOrDefault(element => element.Name.LocalName == "rootfile")?.Attribute("full-path") ?? "";
        if (!PreviewPackage.IsSafeMember(packageName)) throw new InvalidDataException("Invalid EPUB package path.");
        var package = PreviewPackage.ReadXml(archive.GetEntry(packageName) ?? throw new InvalidDataException("Missing EPUB package."), token);
        var manifest = package.Descendants().Where(element => element.Name.LocalName == "item")
            .ToDictionary(element => (string)element.Attribute("id")!, element => element);
        var parts = new List<PreviewPart>();
        foreach (var reference in package.Descendants().Where(element => element.Name.LocalName == "itemref"))
        {
            if (!manifest.TryGetValue((string?)reference.Attribute("idref") ?? "", out var item) ||
                (string?)item.Attribute("media-type") is not ("application/xhtml+xml" or "text/html")) continue;
            string member = PreviewPackage.ResolveMember(packageName, (string?)item.Attribute("href") ?? "");
            if (archive.GetEntry(member) is not null) parts.Add(new PreviewPart(member, $"{parts.Count + 1}. {Path.GetFileNameWithoutExtension(member)}"));
        }
        if (parts.Count == 0) throw new InvalidDataException("The EPUB contains no readable chapters.");
        var locator = hit?.Snippet?.Locator ?? hit?.Locator;
        string? preferred = selected ?? locator?.ArchiveMember ?? locator?.Section ?? hit?.Anchor?.MemberPath ?? hit?.Anchor?.Section;
        if (preferred is null && hit?.LineContent.StartsWith('[') == true && hit.LineContent.IndexOf(']') is var end && end > 1) preferred = hit.LineContent[1..end];
        string chosen = parts.FirstOrDefault(part => part.Id == preferred)?.Id ?? parts[0].Id;
        var html = Encoding.UTF8.GetString(PreviewPackage.Read(archive.GetEntry(chosen)!, PreviewPackage.MaxXmlBytes, token));
        string safe = content.Sanitize(html, source => PackageImage(archive, chosen, source, token));
        return content.Document("EPUB · Whole chapter · Reflowed content", safe, parts, chosen);
    }

    private static PreviewResource? PackageImage(ZipArchive archive, string parent, string source, CancellationToken token)
    {
        try
        {
            string member = PreviewPackage.ResolveMember(parent, source);
            string? mime = Path.GetExtension(member).ToLowerInvariant() switch
            {
                ".png" => "image/png",
                ".jpg" or ".jpeg" => "image/jpeg",
                ".gif" => "image/gif",
                ".webp" => "image/webp",
                _ => null,
            };
            return mime is null || archive.GetEntry(member) is not { } entry ? null : new PreviewResource(PreviewPackage.Read(entry, 8 * 1024 * 1024, token), mime);
        }
        catch (InvalidDataException) { return null; }
    }

    private static SpreadsheetPreview ReadOds(XDocument document, Hit? hit, CancellationToken token)
    {
        var sheets = new List<PreviewSheet>();
        int cells = 0;
        foreach (var table in document.Descendants(Table + "table"))
        {
            if ((string?)table.Attribute(Table + "display") == "false") continue;
            var rows = new Dictionary<int, IReadOnlyDictionary<int, string>>();
            int rowNumber = 1;
            foreach (var row in table.Descendants(Table + "table-row"))
            {
                token.ThrowIfCancellationRequested();
                int rowRepeat = Repeat(row, "number-rows-repeated", 1_048_576), column = 1;
                var values = new Dictionary<int, string>();
                foreach (var cell in row.Elements().Where(element => element.Name == Table + "table-cell" || element.Name == Table + "covered-table-cell"))
                {
                    int repeat = Repeat(cell, "number-columns-repeated", 16_384);
                    string value = string.Join("\n", cell.Descendants(Text + "p").Select(paragraph => paragraph.Value));
                    if (value.Length == 0) value = (string?)cell.Attribute(Office + "string-value") ?? (string?)cell.Attribute(Office + "value") ??
                        (string?)cell.Attribute(Office + "date-value") ?? (string?)cell.Attribute(Office + "boolean-value") ?? "";
                    if (value.Length > 0)
                        for (int i = 0; i < repeat; i++)
                        {
                            if (++cells > SpreadsheetPreviewService.MaxCells || column + i > 16_384) throw new InvalidDataException("ODS exceeds the cell preview limit.");
                            values.Add(column + i, value);
                        }
                    column = checked(column + repeat);
                }
                if (values.Count > 0)
                    for (int i = 0; i < rowRepeat; i++)
                    {
                        if (rowNumber + i > 1_048_576 || (i > 0 && (cells += values.Count) > SpreadsheetPreviewService.MaxCells)) throw new InvalidDataException("ODS exceeds the row preview limit.");
                        rows.Add(rowNumber + i, values);
                    }
                rowNumber = checked(rowNumber + rowRepeat);
            }
            sheets.Add(new PreviewSheet((string?)table.Attribute(Table + "name") ?? "Sheet", rows, new Dictionary<int, int>()));
        }
        return SpreadsheetPreviewService.SelectTarget(sheets, hit);
    }

    private static int Repeat(XElement element, string name, int maximum) =>
        int.TryParse((string?)element.Attribute(Table + name), out int count) && count > 0 && count <= maximum ? count :
        element.Attribute(Table + name) is null ? 1 : throw new InvalidDataException("Invalid repeated OpenDocument cells.");
}
