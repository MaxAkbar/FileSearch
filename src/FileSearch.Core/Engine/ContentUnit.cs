using System;
using System.Security.Cryptography;
using System.Text;
using FileSearch.Core.Extractors;

namespace FileSearch.Core.Engine;

public enum ContentUnitKind
{
    Text,
    PdfPage,
    WordParagraph,
    PowerPointShape,
    ExcelRange,
    EmailPart,
    ArchiveMember,
    EpubChapter,
    OpenDocumentPart,
    ImageOcrRegion,
}

public sealed record ContentUnit(
    long Id,
    long FileId,
    ContentUnitKind Kind,
    SourceLocator Locator,
    string Text,
    string ContentHash,
    string Language,
    string ExtractorId,
    string ExtractorVersion)
{
    public static ContentUnit FromTextLine(
        long id,
        long fileId,
        TextLine line,
        string extractorId,
        string extractorVersion,
        string language = "")
    {
        var text = line.Content ?? string.Empty;
        return new ContentUnit(
            id,
            fileId,
            GetKind(line.Anchor),
            SourceLocator.FromAnchor(line.Anchor, line.Number),
            text,
            ComputeContentHash(text),
            language ?? string.Empty,
            extractorId ?? string.Empty,
            extractorVersion ?? string.Empty);
    }

    private static ContentUnitKind GetKind(SourceAnchor? anchor) =>
        anchor?.Kind switch
        {
            SourceAnchorKind.Pdf => anchor.X is null ? ContentUnitKind.PdfPage : ContentUnitKind.ImageOcrRegion,
            SourceAnchorKind.Word => ContentUnitKind.WordParagraph,
            SourceAnchorKind.PowerPoint => ContentUnitKind.PowerPointShape,
            SourceAnchorKind.Excel => ContentUnitKind.ExcelRange,
            SourceAnchorKind.Email => ContentUnitKind.EmailPart,
            SourceAnchorKind.Archive => ContentUnitKind.ArchiveMember,
            SourceAnchorKind.Epub => ContentUnitKind.EpubChapter,
            SourceAnchorKind.OpenDocument => ContentUnitKind.OpenDocumentPart,
            SourceAnchorKind.ImageOcr => ContentUnitKind.ImageOcrRegion,
            _ => ContentUnitKind.Text,
        };

    private static string ComputeContentHash(string text)
    {
        var bytes = SHA256.HashData(Encoding.UTF8.GetBytes(text));
        return Convert.ToHexString(bytes).ToLowerInvariant();
    }
}
