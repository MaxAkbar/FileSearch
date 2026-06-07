using System;
using System.Collections.Generic;
using System.IO;
using Color = System.Windows.Media.Color;

namespace FileSearch.Gui.Converters;

/// <summary>
/// Maps a file extension to a display label and accent color for the
/// little square type-badge shown on result cards and in the preview.
/// Colors mirror the Atlas design palette; unknown types fall back to a
/// neutral grey with the upper-cased extension as the label.
/// </summary>
public static class FileTypeCatalog
{
    private sealed record TypeInfo(Color Color, string Label);

    private static readonly TypeInfo Fallback = new(Color.FromRgb(0x88, 0x88, 0x88), string.Empty);

    private static readonly Dictionary<string, TypeInfo> Map =
        new(StringComparer.OrdinalIgnoreCase)
        {
            ["pdf"] = new(Hex(0xE0524A), "PDF"),
            ["doc"] = new(Hex(0x2B88D8), "DOC"),
            ["docx"] = new(Hex(0x2B88D8), "DOC"),
            ["rtf"] = new(Hex(0x2B88D8), "RTF"),
            ["xls"] = new(Hex(0x1F9D62), "XLS"),
            ["xlsx"] = new(Hex(0x1F9D62), "XLS"),
            ["csv"] = new(Hex(0x1F9D62), "CSV"),
            ["ppt"] = new(Hex(0xE0703A), "PPT"),
            ["pptx"] = new(Hex(0xE0703A), "PPT"),
            ["txt"] = new(Hex(0x7A7F87), "TXT"),
            ["log"] = new(Hex(0x7A7F87), "LOG"),
            ["md"] = new(Hex(0x5A6472), "MD"),
            ["cs"] = new(Hex(0x9B4DCA), "C#"),
            ["xaml"] = new(Hex(0x9B4DCA), "XAML"),
            ["csproj"] = new(Hex(0x9B4DCA), "PROJ"),
            ["sln"] = new(Hex(0x9B4DCA), "SLN"),
            ["slnx"] = new(Hex(0x9B4DCA), "SLN"),
            ["json"] = new(Hex(0xE0A72C), "{ }"),
            ["yml"] = new(Hex(0xE0A72C), "YML"),
            ["yaml"] = new(Hex(0xE0A72C), "YML"),
            ["xml"] = new(Hex(0xE0A72C), "XML"),
            ["html"] = new(Hex(0xE0623A), "</>"),
            ["htm"] = new(Hex(0xE0623A), "</>"),
            ["css"] = new(Hex(0x2B88D8), "CSS"),
            ["js"] = new(Hex(0xE0A72C), "JS"),
            ["ts"] = new(Hex(0x2B88D8), "TS"),
            ["svg"] = new(Hex(0x3AA86A), "SVG"),
            ["png"] = new(Hex(0x36C275), "PNG"),
            ["jpg"] = new(Hex(0x2FAE66), "JPG"),
            ["jpeg"] = new(Hex(0x2FAE66), "JPG"),
            ["gif"] = new(Hex(0x36C275), "GIF"),
            ["mp4"] = new(Hex(0xE0843A), "MP4"),
            ["mp3"] = new(Hex(0xCF6A2E), "MP3"),
            ["zip"] = new(Hex(0x8A929C), "ZIP"),
            ["7z"] = new(Hex(0x8A929C), "7Z"),
            ["rar"] = new(Hex(0x8A929C), "RAR"),
        };

    public static Color GetColor(string? fileName) => Lookup(fileName).Color;

    public static string GetLabel(string? fileName)
    {
        var info = Lookup(fileName, out var ext);
        if (!string.IsNullOrEmpty(info.Label))
            return info.Label;

        // Unknown type: show up to four upper-cased extension chars, or a
        // generic glyph for extensionless files.
        if (string.IsNullOrEmpty(ext))
            return "·";
        return ext.Length <= 4 ? ext.ToUpperInvariant() : ext[..4].ToUpperInvariant();
    }

    private static TypeInfo Lookup(string? fileName) => Lookup(fileName, out _);

    private static TypeInfo Lookup(string? fileName, out string ext)
    {
        ext = string.IsNullOrEmpty(fileName)
            ? string.Empty
            : Path.GetExtension(fileName).TrimStart('.');
        return Map.TryGetValue(ext, out var info) ? info : Fallback;
    }

    private static Color Hex(int rgb) =>
        Color.FromRgb((byte)(rgb >> 16), (byte)(rgb >> 8), (byte)rgb);
}
