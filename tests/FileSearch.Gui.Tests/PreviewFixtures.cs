using System.IO.Compression;
using System.Text;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace FileSearch.Gui.Tests;

internal static class PreviewFixtures
{
    internal static string Package(string root, string name, params (string Name, string Content)[] entries)
    {
        string path = Path.Combine(root, name);
        using var zip = ZipFile.Open(path, ZipArchiveMode.Create);
        foreach (var (member, content) in entries)
        {
            using var writer = new StreamWriter(zip.CreateEntry(member).Open(), new UTF8Encoding(false));
            writer.Write(content);
        }
        return path;
    }

    internal static void Create(string root)
    {
        Directory.CreateDirectory(root);
        WritePdf(Path.Combine(root, "pages.pdf"));
        File.WriteAllText(Path.Combine(root, "table.csv"), "Name,Notes\nAlice,\"First line\nMatching second line\"\nBob,Last row\n");
        Package(root, "workbook.xlsx",
            ("xl/workbook.xml", "<workbook xmlns='http://schemas.openxmlformats.org/spreadsheetml/2006/main' xmlns:r='http://schemas.openxmlformats.org/officeDocument/2006/relationships'><sheets><sheet name='Intro' sheetId='1' r:id='r1'/><sheet name='Data' sheetId='2' r:id='r2'/></sheets></workbook>"),
            ("xl/_rels/workbook.xml.rels", "<Relationships xmlns='http://schemas.openxmlformats.org/package/2006/relationships'><Relationship Id='r1' Target='worksheets/sheet1.xml'/><Relationship Id='r2' Target='/xl/worksheets/sheet2.xml'/></Relationships>"),
            ("xl/worksheets/sheet1.xml", "<worksheet xmlns='http://schemas.openxmlformats.org/spreadsheetml/2006/main'><sheetData><row r='1'><c r='A1' t='inlineStr'><is><t>Introduction</t></is></c></row></sheetData></worksheet>"),
            ("xl/worksheets/sheet2.xml", "<worksheet xmlns='http://schemas.openxmlformats.org/spreadsheetml/2006/main'><sheetData><row r='501'><c r='B501' t='inlineStr'><is><t>First</t></is></c><c r='AZ501' t='inlineStr'><is><t>Matching cell</t></is></c><c r='BA501'><f>1+2</f><v>3</v></c><c r='BB501'><f>HYPERLINK(&quot;https://example.invalid&quot;)</f></c></row></sheetData></worksheet>"));
        Package(root, "book.epub",
            ("META-INF/container.xml", "<container><rootfiles><rootfile full-path='Book/package.opf'/></rootfiles></container>"),
            ("Book/package.opf", "<package><manifest><item id='two' href='two.xhtml' media-type='application/xhtml+xml'/><item id='one' href='one.xhtml' media-type='application/xhtml+xml'/></manifest><spine><itemref idref='one'/><itemref idref='two'/></spine></package>"),
            ("Book/one.xhtml", "<html><body><h1>First chapter</h1><p>Complete chapter beginning</p><p>Complete chapter ending</p></body></html>"),
            ("Book/two.xhtml", "<html><body><h1>Second chapter</h1><strong>Matching chapter</strong><p>End of whole chapter</p><script>window.previewUnsafe=true</script><img src='https://example.invalid/tracker.png'/></body></html>"));
        string start = "<office:document-content xmlns:office='urn:oasis:names:tc:opendocument:xmlns:office:1.0' xmlns:text='urn:oasis:names:tc:opendocument:xmlns:text:1.0' xmlns:table='urn:oasis:names:tc:opendocument:xmlns:table:1.0' xmlns:draw='urn:oasis:names:tc:opendocument:xmlns:drawing:1.0'><office:body>";
        const string end = "</office:body></office:document-content>";
        Package(root, "document.odt", ("content.xml", start + "<office:text><text:h>Document title</text:h><text:p>Whole paragraph with <text:span>formatted content</text:span>.</text:p><table:table><table:table-row><table:table-cell><text:p>Table value</text:p></table:table-cell></table:table-row></table:table></office:text>" + end));
        Package(root, "slides.odp", ("content.xml", start + "<office:presentation><draw:page draw:name='First slide'><text:p>Whole first slide</text:p></draw:page><draw:page draw:name='Second slide'><text:p>Whole second slide</text:p></draw:page></office:presentation>" + end));
        Package(root, "spreadsheet.ods", ("content.xml", start + "<office:spreadsheet><table:table table:name='Data'><table:table-row table:number-rows-repeated='500'/><table:table-row><table:table-cell table:number-columns-repeated='51'/><table:table-cell><text:p>Matching ODS cell</text:p></table:table-cell></table:table-row><table:table-row table:number-rows-repeated='1000000'/></table:table></office:spreadsheet>" + end));
        File.WriteAllText(Path.Combine(root, "formatted.rtf"), "{\\rtf1\\ansi A complete \\b formatted\\b0  document.\\par Final paragraph.}");
        File.WriteAllText(Path.Combine(root, "message.eml"), "From: Sender <sender@example.invalid>\r\nTo: Reader <reader@example.invalid>\r\nSubject: Complete message\r\nMIME-Version: 1.0\r\nContent-Type: text/html; charset=utf-8\r\n\r\n<h1>Complete mail body</h1><p>Beginning of message</p>" + new string('x', 1500) + "<strong>End of whole message</strong><script>window.previewUnsafe=true</script><img src='https://example.invalid/tracker.png'>");
        WriteFrames(Path.Combine(root, "multipage.tiff"), new TiffBitmapEncoder());
        WriteFrames(Path.Combine(root, "animated.gif"), new GifBitmapEncoder());
        // Lossless 1x1 WebP, decoded by the Windows codec without a PowerToys dependency.
        File.WriteAllBytes(Path.Combine(root, "image.webp"), Convert.FromBase64String("UklGRhoAAABXRUJQVlA4TA0AAAAvAAAAEAcQERGIiP4HAA=="));
        Package(root, "members.zip", ("readme.txt", "First member"), ("folder/match.txt", "Beginning of member\nMatching archive member\nEnd of whole member"), ("../unsafe.txt", "This entry must be excluded"));
    }

    private static void WriteFrames(string path, BitmapEncoder encoder)
    {
        foreach (var pixel in new[] { new byte[] { 0, 0, 255, 255 }, new byte[] { 255, 0, 0, 255 } })
        {
            var bitmap = BitmapSource.Create(1, 1, 96, 96, PixelFormats.Bgra32, null, pixel, 4);
            encoder.Frames.Add(BitmapFrame.Create(bitmap));
        }
        using var stream = File.Create(path);
        encoder.Save(stream);
    }

    internal static void WritePdf(string path)
    {
        static string PdfStream(string value) => $"<< /Length {value.Length} >>\nstream\n{value}\nendstream";
        string[] objects =
        [
            "<< /Type /Catalog /Pages 2 0 R >>", "<< /Type /Pages /Kids [3 0 R 5 0 R] /Count 2 >>",
            "<< /Type /Page /Parent 2 0 R /MediaBox [0 0 200 200] /Resources << /Font << /F1 7 0 R >> >> /Contents 4 0 R >>",
            PdfStream("BT /F1 24 Tf 20 100 Td (First page) Tj ET"),
            "<< /Type /Page /Parent 2 0 R /MediaBox [0 0 200 200] /Resources << /Font << /F1 7 0 R >> >> /Contents 6 0 R >>",
            PdfStream("q 1 0 0 rg 0 0 12 12 re f 188 0 12 12 re f 0 188 12 12 re f 188 188 12 12 re f Q BT /F1 24 Tf 20 100 Td (Second page) Tj ET"),
            "<< /Type /Font /Subtype /Type1 /BaseFont /Helvetica >>",
        ];
        var pdf = new StringBuilder("%PDF-1.4\n");
        var offsets = new List<int>();
        for (int i = 0; i < objects.Length; i++) { offsets.Add(pdf.Length); pdf.Append($"{i + 1} 0 obj\n{objects[i]}\nendobj\n"); }
        int xref = pdf.Length;
        pdf.Append($"xref\n0 {objects.Length + 1}\n0000000000 65535 f \n");
        foreach (int offset in offsets) pdf.Append($"{offset:0000000000} 00000 n \n");
        pdf.Append($"trailer\n<< /Size {objects.Length + 1} /Root 1 0 R >>\nstartxref\n{xref}\n%%EOF\n");
        File.WriteAllText(path, pdf.ToString(), Encoding.ASCII);
    }
}
