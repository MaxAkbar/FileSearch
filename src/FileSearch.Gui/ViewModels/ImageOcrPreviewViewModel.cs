using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using FileSearch.Core.Engine;
using FileSearch.Core.Extractors;
using FileSearch.WindowsOcr;

namespace FileSearch.Gui.ViewModels;

public sealed class ImageOcrPreviewViewModel
{
    private ImageOcrPreviewViewModel(
        string imagePath,
        string label,
        int sourceWidth,
        int sourceHeight,
        int x,
        int y,
        int width,
        int height)
    {
        ImagePath = imagePath;
        Label = label;
        SourceWidth = sourceWidth;
        SourceHeight = sourceHeight;
        X = x;
        Y = y;
        Width = width;
        Height = height;
    }

    public string ImagePath { get; }

    public string Label { get; }

    public int SourceWidth { get; }

    public int SourceHeight { get; }

    public int X { get; }

    public int Y { get; }

    public int Width { get; }

    public int Height { get; }

    public static ImageOcrPreviewViewModel? TryCreate(string path, IEnumerable<Hit> hits)
    {
        if (string.IsNullOrWhiteSpace(path) || !File.Exists(path))
            return null;

        var anchor = hits
            .Select(hit => hit.Anchor)
            .FirstOrDefault(IsStandaloneImageOcrAnchor);
        if (anchor is null)
            return null;

        return Create(path, anchor);
    }

    public static bool HasPreviewAnchor(IEnumerable<Hit> hits) =>
        hits.Any(hit => IsPreviewAnchor(hit.Anchor));

    public static bool IsPreviewAnchor(SourceAnchor? anchor) =>
        IsStandaloneImageOcrAnchor(anchor) ||
        IsPdfOcrAnchor(anchor) ||
        IsEmbeddedOcrAnchor(anchor);

    public static async Task<ImageOcrPreviewViewModel?> TryCreateAsync(
        string path,
        IEnumerable<Hit> hits,
        CancellationToken cancellationToken)
    {
        var hitList = hits as IReadOnlyCollection<Hit> ?? hits.ToList();
        var imagePreview = TryCreate(path, hitList);
        if (imagePreview is not null)
            return imagePreview;

        var embeddedAnchor = hitList
            .Select(hit => hit.Anchor)
            .FirstOrDefault(IsEmbeddedOcrAnchor);
        if (embeddedAnchor is not null)
        {
            var embeddedImagePath = await EmbeddedOcrImagePreviewRenderer
                .ExtractAsync(path, embeddedAnchor, cancellationToken)
                .ConfigureAwait(false);
            if (embeddedImagePath is not null)
                return Create(embeddedImagePath, embeddedAnchor);
        }

        if (string.IsNullOrWhiteSpace(path) ||
            !File.Exists(path) ||
            !string.Equals(Path.GetExtension(path), ".pdf", StringComparison.OrdinalIgnoreCase))
        {
            return null;
        }

        var anchor = hitList
            .Select(hit => hit.Anchor)
            .FirstOrDefault(IsPdfOcrAnchor);
        if (anchor is null)
            return null;

        var sourceWidth = anchor.SourceWidth ?? 0;
        var sourceHeight = anchor.SourceHeight ?? 0;
        if (sourceWidth <= 0 || sourceHeight <= 0)
            return null;

        var imagePath = await PdfOcrPagePreviewRenderer
            .RenderPageAsync(path, anchor.Page!.Value, sourceWidth, sourceHeight, cancellationToken)
            .ConfigureAwait(false);
        return imagePath is null ? null : Create(imagePath, anchor);
    }

    private static bool IsStandaloneImageOcrAnchor(SourceAnchor? anchor) =>
        anchor is
        {
            Kind: SourceAnchorKind.ImageOcr,
            X: not null,
            Y: not null,
            Width: > 0,
            Height: > 0,
            SourceWidth: > 0,
            SourceHeight: > 0,
        };

    private static bool IsPdfOcrAnchor(SourceAnchor? anchor) =>
        anchor is
        {
            Kind: SourceAnchorKind.Pdf,
            Page: > 0,
            X: not null,
            Y: not null,
            Width: > 0,
            Height: > 0,
            SourceWidth: > 0,
            SourceHeight: > 0,
        };

    private static bool IsEmbeddedOcrAnchor(SourceAnchor? anchor) =>
        anchor is
        {
            MemberPath: not null,
            X: not null,
            Y: not null,
            Width: > 0,
            Height: > 0,
            SourceWidth: > 0,
            SourceHeight: > 0,
        } &&
        anchor.Kind is SourceAnchorKind.Word or
            SourceAnchorKind.PowerPoint or
            SourceAnchorKind.Excel or
            SourceAnchorKind.Email or
            SourceAnchorKind.Archive or
            SourceAnchorKind.Epub or
            SourceAnchorKind.OpenDocument;

    private static ImageOcrPreviewViewModel? Create(string imagePath, SourceAnchor anchor)
    {
        var sourceWidth = anchor.SourceWidth ?? 0;
        var sourceHeight = anchor.SourceHeight ?? 0;
        if (sourceWidth <= 0 || sourceHeight <= 0)
            return null;

        var x = Math.Clamp(anchor.X ?? 0, 0, sourceWidth);
        var y = Math.Clamp(anchor.Y ?? 0, 0, sourceHeight);
        var right = Math.Clamp(x + Math.Max(1, anchor.Width ?? 1), x, sourceWidth);
        var bottom = Math.Clamp(y + Math.Max(1, anchor.Height ?? 1), y, sourceHeight);

        return new ImageOcrPreviewViewModel(
            imagePath,
            anchor.DisplayText,
            sourceWidth,
            sourceHeight,
            x,
            y,
            Math.Max(1, right - x),
            Math.Max(1, bottom - y));
    }
}

internal static class PdfOcrPagePreviewRenderer
{
    public static async Task<string?> RenderPageAsync(
        string pdfPath,
        int pageNumber,
        int sourceWidth,
        int sourceHeight,
        CancellationToken cancellationToken)
    {
        try
        {
            if (pageNumber <= 0 || sourceWidth <= 0 || sourceHeight <= 0)
                return null;

            cancellationToken.ThrowIfCancellationRequested();
            var cachePath = GetCachePath(pdfPath, pageNumber, sourceWidth, sourceHeight);
            var pdfModifiedUtc = File.GetLastWriteTimeUtc(pdfPath);
            if (File.Exists(cachePath) && File.GetLastWriteTimeUtc(cachePath) >= pdfModifiedUtc)
                return cachePath;

            Directory.CreateDirectory(Path.GetDirectoryName(cachePath)!);
            var rendered = await WindowsPdfRenderer.RenderAsync(pdfPath, pageNumber, cancellationToken,
                sourceWidth, sourceHeight, fitBounds: false).ConfigureAwait(false);
            if (rendered.Page != pageNumber)
                return null;
            cancellationToken.ThrowIfCancellationRequested();
            var temporaryPath = cachePath + "." + Guid.NewGuid().ToString("N") + ".tmp";
            try
            {
                await File.WriteAllBytesAsync(temporaryPath, rendered.PngBytes, cancellationToken).ConfigureAwait(false);
                cancellationToken.ThrowIfCancellationRequested();
                File.Move(temporaryPath, cachePath, overwrite: true);
            }
            finally { File.Delete(temporaryPath); }
            return cachePath;
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch
        {
            return null;
        }
    }

    private static string GetCachePath(string pdfPath, int pageNumber, int sourceWidth, int sourceHeight)
    {
        var input = $"{Path.GetFullPath(pdfPath)}|{File.GetLastWriteTimeUtc(pdfPath).Ticks}|{pageNumber}|{sourceWidth}|{sourceHeight}";
        var hash = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(input)));
        return Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "FileSearch",
            "PreviewCache",
            $"pdf-{hash}.png");
    }
}
