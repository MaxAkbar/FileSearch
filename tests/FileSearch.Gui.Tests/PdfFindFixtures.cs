using System.Globalization;
using System.Text;

namespace FileSearch.Gui.Tests;

/// <summary>Small uncompressed PDFs whose source coordinates and ink are known independently of either PDF library.</summary>
internal static class PdfFindFixtures
{
    internal const double CropWidth = 420;
    internal const double CropHeight = 320;

    internal static string Write(string path, int rotation = 0, bool crop = true, bool rotateText = false,
        bool includeText = true)
    {
        double left = crop ? 40 : 0, bottom = crop ? 50 : 0;
        double width = crop ? CropWidth : 500, height = crop ? CropHeight : 400;
        string N(double value) => value.ToString(CultureInfo.InvariantCulture);
        var contents = new StringBuilder();
        // Distinct colored corner squares establish the complete visible crop and its rotation.
        contents.AppendLine($"q 1 0 0 rg {N(left + 2)} {N(bottom + 2)} 12 12 re f Q");
        contents.AppendLine($"q 0 1 0 rg {N(left + 2)} {N(bottom + height - 14)} 12 12 re f Q");
        contents.AppendLine($"q 0 0 1 rg {N(left + width - 14)} {N(bottom + 2)} 12 12 re f Q");
        contents.AppendLine($"q 1 0 1 rg {N(left + width - 14)} {N(bottom + height - 14)} 12 12 re f Q");
        if (includeText)
        {
            // H is the only dark ink in the central page. Other text uses blue,
            // allowing tests to inspect actual raster glyph ink without font-metric assumptions.
            string matrix = rotateText ? "0 1 -1 0" : "1 0 0 1";
            contents.AppendLine($"q 0 0 0 rg BT /F1 40 Tf {matrix} {N(left + 100)} {N(bottom + 140)} Tm (H) Tj ET Q");
            contents.AppendLine($"q 0 0 1 rg BT /F1 14 Tf {N(left + 40)} {N(bottom + 50)} Td (needle phrase) Tj ET Q");
            if (crop)
                contents.AppendLine("q 0 0 0 rg BT /F1 12 Tf 12 24 Td (OUTSIDE) Tj ET Q");
        }
        string stream = contents.ToString();
        string media = crop ? "10 20 510 420" : "0 0 500 400";
        string cropBox = crop ? " /CropBox [40 50 460 370]" : "";
        string[] objects =
        [
            "<< /Type /Catalog /Pages 2 0 R >>",
            "<< /Type /Pages /Kids [3 0 R] /Count 1 >>",
            $"<< /Type /Page /Parent 2 0 R /MediaBox [{media}]{cropBox} /Rotate {rotation} /Resources << /Font << /F1 5 0 R >> >> /Contents 4 0 R >>",
            $"<< /Length {Encoding.ASCII.GetByteCount(stream)} >>\nstream\n{stream}endstream",
            "<< /Type /Font /Subtype /Type1 /BaseFont /Helvetica >>",
        ];
        return WriteObjects(path, objects);
    }

    internal static string WriteTwoPages(string path)
    {
        static string Stream(string text) => $"<< /Length {Encoding.ASCII.GetByteCount(text)} >>\nstream\n{text}\nendstream";
        string[] objects =
        [
            "<< /Type /Catalog /Pages 2 0 R >>",
            "<< /Type /Pages /Kids [3 0 R 5 0 R] /Count 2 >>",
            "<< /Type /Page /Parent 2 0 R /MediaBox [0 0 300 240] /Resources << /Font << /F1 7 0 R >> >> /Contents 4 0 R >>",
            Stream("q 1 0 0 rg 2 2 10 10 re f 288 2 10 10 re f 2 228 10 10 re f 288 228 10 10 re f Q BT /F1 16 Tf 25 190 Td (needle phrase) Tj 0 -40 Td (needle phrase) Tj 0 -40 Td (NEEDLE PHRASE) Tj 0 -40 Td (john OR smith) Tj ET"),
            "<< /Type /Page /Parent 2 0 R /MediaBox [0 0 300 240] /Resources << /Font << /F1 7 0 R >> >> /Contents 6 0 R >>",
            Stream("q 0 0 1 rg 2 2 10 10 re f 288 2 10 10 re f 2 228 10 10 re f 288 228 10 10 re f Q BT /F1 16 Tf 25 190 Td (second page needle phrase) Tj 0 -40 Td (second page final line) Tj ET"),
            "<< /Type /Font /Subtype /Type1 /BaseFont /Helvetica >>",
        ];
        return WriteObjects(path, objects);
    }

    private static string WriteObjects(string path, string[] objects)
    {
        var pdf = new StringBuilder("%PDF-1.4\n");
        var offsets = new List<int>();
        for (int index = 0; index < objects.Length; index++)
        {
            offsets.Add(Encoding.ASCII.GetByteCount(pdf.ToString()));
            pdf.Append(CultureInfo.InvariantCulture, $"{index + 1} 0 obj\n{objects[index]}\nendobj\n");
        }
        int xref = Encoding.ASCII.GetByteCount(pdf.ToString());
        pdf.Append(CultureInfo.InvariantCulture, $"xref\n0 {objects.Length + 1}\n0000000000 65535 f \n");
        foreach (int offset in offsets) pdf.Append(CultureInfo.InvariantCulture, $"{offset:0000000000} 00000 n \n");
        pdf.Append(CultureInfo.InvariantCulture, $"trailer\n<< /Size {objects.Length + 1} /Root 1 0 R >>\nstartxref\n{xref}\n%%EOF\n");
        File.WriteAllText(path, pdf.ToString(), Encoding.ASCII);
        return path;
    }
}
