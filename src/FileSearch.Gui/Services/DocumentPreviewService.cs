using System.IO;
using System.Text;
using System.Windows.Media.Imaging;
using System.Xml;
using System.Xml.Linq;
using FileSearch.Core.Databases;
using FileSearch.Core.Engine;
using FileSearch.Core.Extractors;
using FileSearch.WindowsOcr;
using Markdig;

namespace FileSearch.Gui.Services;

public enum DocumentPreviewKind { Details, Image, Pdf, Text, Html, Shell, Spreadsheet, Archive, Database }

public sealed record PreviewResource(byte[] Bytes, string ContentType);
public sealed record PreviewPart(string Id, string Title);

public sealed record DocumentPreview(
    DocumentPreviewKind Kind, string Summary, string Text = "", BitmapSource? Image = null,
    int Page = 1, int PageCount = 0, bool IsTruncated = false,
    SpreadsheetPreview? Workbook = null, IReadOnlyList<PreviewPart>? Parts = null, string? SelectedPart = null,
    IReadOnlyDictionary<string, PreviewResource>? Resources = null, PreviewResource? Animation = null)
{
    internal PreviewSourceVersion? PdfSourceVersion { get; init; }
    internal System.Windows.Size PdfPageSize { get; init; }
}

/// <summary>Bounded, read-only previews of the original file, independent of search hits.</summary>
public static class DocumentPreviewService
{
    public const int MaxTextCharacters = 250_000;
    public const long MaxTextBytes = 4 * 1024 * 1024;
    public const long MaxImageBytes = 32 * 1024 * 1024;
    public const long MaxPdfBytes = 100 * 1024 * 1024;
    private static readonly HashSet<string> ImageExtensions = new(StringComparer.OrdinalIgnoreCase)
        { ".png", ".jpg", ".jpeg", ".bmp", ".gif", ".tif", ".tiff", ".ico", ".wdp" };
    private static readonly HashSet<string> TextExtensions = new(StringComparer.OrdinalIgnoreCase)
        { ".txt", ".log", ".csv", ".tsv", ".cs", ".csx", ".vb", ".fs", ".js", ".jsx", ".ts", ".tsx", ".json", ".jsonc", ".xml", ".xaml", ".html", ".htm", ".css", ".scss", ".sql", ".py", ".rs", ".go", ".java", ".c", ".cpp", ".h", ".hpp", ".ps1", ".psm1", ".sh", ".bat", ".cmd", ".yml", ".yaml", ".toml", ".ini", ".config", ".props", ".targets", ".csproj", ".sln", ".slnx", ".md", ".markdown", ".svg" };
    private static readonly MarkdownPipeline MarkdownPipeline = new MarkdownPipelineBuilder()
        .UsePipeTables().UseTaskLists().DisableHtml().Build();

    internal static string? MatchedText(Hit? hit)
    {
        if (hit?.Highlights is not { Count: > 0 }) return null;
        var span = hit.Highlights[0];
        if (span.Length <= 0 || span.Start < 0 || span.Start > hit.LineContent.Length - span.Length) return null;
        return hit.LineContent.Substring(span.Start, span.Length);
    }

    public static int FindInitialPdfPage(IEnumerable<Hit> hits, Hit? preferredHit = null)
    {
        static int? Page(Hit hit) => hit.Snippet?.Locator?.Page is > 0 ? hit.Snippet.Locator.Page :
            hit.Locator?.Page is > 0 ? hit.Locator.Page : hit.Anchor?.Page is > 0 ? hit.Anchor.Page : null;
        if (preferredHit is { Kind: HitKind.Content } && Page(preferredHit) is { } preferred) return preferred;
        foreach (var hit in hits)
            if (hit.Kind == HitKind.Content && Page(hit) is { } page) return page;
        return 1;
    }

    public static Task<DocumentPreview> LoadAsync(string path, CancellationToken cancellationToken, Hit? hit = null,
        string? selectedPart = null, int page = 1, MailMessageMetadata? mail = null) =>
        Task.Run(async () =>
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (Directory.Exists(path))
                return new DocumentPreview(DocumentPreviewKind.Details, "Folder", "Select a file to preview its contents.");
            var file = new FileInfo(path);
            if (!file.Exists)
                return new DocumentPreview(DocumentPreviewKind.Details, "File unavailable", "The file has moved or is no longer accessible.");
            var summary = $"{file.Extension.ToUpperInvariant().TrimStart('.')} · {file.Length:n0} bytes · Modified {file.LastWriteTime:g}";
            if (DatabaseFiles.IsDatabase(path))
                return new DocumentPreview(DocumentPreviewKind.Database, $"{DatabaseFiles.Detect(path)} · Read-only database tables");
            switch (file.Extension.ToLowerInvariant())
            {
                case ".csv":
                case ".tsv":
                case ".xlsx":
                    return new DocumentPreview(DocumentPreviewKind.Spreadsheet, summary,
                        Workbook: await SpreadsheetPreviewService.LoadAsync(path, hit, cancellationToken).ConfigureAwait(false));
                case ".zip":
                    return await ArchivePreviewService.LoadAsync(path, hit, selectedPart, cancellationToken).ConfigureAwait(false);
                case ".epub":
                case ".odt":
                case ".ods":
                case ".odp":
                case ".rtf":
                    return await BookPreviewService.LoadAsync(path, hit, selectedPart, cancellationToken).ConfigureAwait(false);
                case ".eml":
                case ".msg":
                    return await MailPreviewService.LoadAsync(path, mail, cancellationToken).ConfigureAwait(false);
                case ".pst":
                case ".ost":
                    return mail is null ? Limit(summary, "Select a message to preview its contents.") :
                        await MailPreviewService.LoadAsync(path, mail, cancellationToken).ConfigureAwait(false);
                case ".webp":
                case ".gif":
                case ".tif":
                case ".tiff":
                    return await ImagePreviewService.LoadAsync(path, page, cancellationToken).ConfigureAwait(false);
            }
            if (ImageExtensions.Contains(file.Extension))
            {
                if (file.Length > MaxImageBytes) return Limit(summary, "Image previews are limited to 32 MB.");
                using var stream = OpenRead(path);
                var image = DecodeImage(stream);
                return new DocumentPreview(DocumentPreviewKind.Image, summary, Image: image);
            }
            if (file.Extension.Equals(".pdf", StringComparison.OrdinalIgnoreCase))
            {
                if (file.Length > MaxPdfBytes) return Limit(summary, "PDF previews are limited to 100 MB.");
                return await LoadPdfPageAsync(path, page, cancellationToken).ConfigureAwait(false);
            }
            if (!TextExtensions.Contains(file.Extension) && NativePreviewHandler.Find(file.Extension) is not null)
                return new DocumentPreview(DocumentPreviewKind.Shell, summary);
            if (file.Length > MaxTextBytes) return Limit(summary, "Text previews are limited to 4 MB. Use Matches or Open for this file.");

            using var reader = new StreamReader(OpenRead(path), new UTF8Encoding(false, true), detectEncodingFromByteOrderMarks: true);
            var buffer = new char[MaxTextCharacters + 1];
            int count;
            try { count = await reader.ReadBlockAsync(buffer.AsMemory(), cancellationToken).ConfigureAwait(false); }
            catch (DecoderFallbackException) { return Limit(summary, "No file preview is available. Use Matches or Open."); }
            var text = new string(buffer, 0, Math.Min(count, MaxTextCharacters));
            if (text.Any(character => char.IsControl(character) && character is not '\r' and not '\n' and not '\t' and not '\f'))
                return Limit(summary, "No file preview is available. Use Matches or Open.");
            var truncated = count > MaxTextCharacters;
            if (file.Extension.Equals(".md", StringComparison.OrdinalIgnoreCase) || file.Extension.Equals(".markdown", StringComparison.OrdinalIgnoreCase))
                return new DocumentPreview(DocumentPreviewKind.Html, summary, CreateMarkdownHtml(text), IsTruncated: truncated);
            if (file.Extension.Equals(".svg", StringComparison.OrdinalIgnoreCase) && !truncated)
                return new DocumentPreview(DocumentPreviewKind.Html, summary, CreateSvgHtml(text));
            return new DocumentPreview(DocumentPreviewKind.Text, summary, text, IsTruncated: truncated);
        }, cancellationToken);

    public static Task<DocumentPreview> LoadPdfPageAsync(string path, int pageNumber, CancellationToken token) =>
        Task.Run(async () =>
        {
            token.ThrowIfCancellationRequested();
            if (new FileInfo(path).Length > MaxPdfBytes) return Limit("PDF", "PDF previews are limited to 100 MB.");
            var sourceVersion = PreviewSourceVersion.Read(path);
            var rendered = await WindowsPdfRenderer.RenderAsync(path, Math.Max(1, pageNumber), token).ConfigureAwait(false);
            token.ThrowIfCancellationRequested();
            using var input = new MemoryStream(rendered.PngBytes, writable: false);
            var image = DecodeImage(input);
            return new DocumentPreview(DocumentPreviewKind.Pdf, $"PDF · {rendered.PageCount:n0} pages", Image: image,
                Page: rendered.Page, PageCount: rendered.PageCount)
            {
                PdfSourceVersion = sourceVersion == PreviewSourceVersion.Read(path) ? sourceVersion : null,
                PdfPageSize = new System.Windows.Size(rendered.Width, rendered.Height),
            };
        }, token);

    private static FileStream OpenRead(string path) => new(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);

    private static BitmapImage DecodeImage(Stream stream)
    {
        var decoder = BitmapDecoder.Create(stream, BitmapCreateOptions.DelayCreation, BitmapCacheOption.None);
        var frame = decoder.Frames[0];
        if ((long)frame.PixelWidth * frame.PixelHeight > 50_000_000)
            throw new InvalidDataException("The image is too large to preview.");
        double scale = Math.Min(1, Math.Min(1600d / frame.PixelWidth, 2000d / frame.PixelHeight));
        stream.Position = 0;
        var image = new BitmapImage();
        image.BeginInit();
        image.CacheOption = BitmapCacheOption.OnLoad;
        image.DecodePixelWidth = Math.Max(1, (int)(frame.PixelWidth * scale));
        image.DecodePixelHeight = Math.Max(1, (int)(frame.PixelHeight * scale));
        image.StreamSource = stream;
        image.EndInit();
        image.Freeze();
        return image;
    }

    private static DocumentPreview Limit(string summary, string message) => new(DocumentPreviewKind.Details, summary, message);

    public static string CreateMarkdownHtml(string text) => WrapHtml(Markdown.ToHtml(text, MarkdownPipeline));

    public static string CreateSvgHtml(string text)
    {
        using var reader = XmlReader.Create(new StringReader(text), new XmlReaderSettings { DtdProcessing = DtdProcessing.Prohibit, XmlResolver = null });
        var svg = XDocument.Load(reader);
        if (svg.Root?.Name != XName.Get("svg", "http://www.w3.org/2000/svg")) throw new InvalidDataException("Invalid SVG image.");
        foreach (var node in svg.Descendants().Where(node => node.Name.LocalName is "script" or "foreignObject" or "iframe" or "object").ToArray()) node.Remove();
        foreach (var attribute in svg.Descendants().Attributes().Where(attribute =>
            attribute.Name.LocalName.StartsWith("on", StringComparison.OrdinalIgnoreCase) ||
            (attribute.Name.LocalName is "href" or "src" && !attribute.Value.StartsWith('#'))).ToArray()) attribute.Remove();
        return WrapHtml(svg.Root.ToString(SaveOptions.DisableFormatting));
    }

    internal static string WrapHtml(string body) => """
        <!doctype html><html><head><meta charset="utf-8">
        <meta http-equiv="Content-Security-Policy" content="default-src 'none'; style-src 'unsafe-inline'; img-src data: https://filesearch-preview.invalid; base-uri 'none'; form-action 'none'">
        <meta name="color-scheme" content="light dark"><style>
        :root{color-scheme:light dark}body{font:15px/1.6 'Segoe UI',sans-serif;margin:24px;overflow-wrap:anywhere}
        pre,code{font-family:Consolas,monospace}pre{padding:14px;overflow:auto;background:light-dark(#f2f4f6,#22262b)}
        table{border-collapse:collapse}td,th{border:1px solid #888;padding:6px 10px}img,svg{max-width:100%;height:auto}
        a{color:light-dark(#0866a6,#75c8ff)}blockquote{border-left:3px solid #888;padding-left:14px;margin-left:0}
        </style></head><body>
        """ + body + "</body></html>";
}
