using System.Security.Cryptography;
using System.Text;

namespace FileSearch.Core.Engine;

public sealed record ContentChunk(
    string ChunkKey,
    long FileId,
    IReadOnlyList<long> ContentUnitIds,
    SourceLocator Locator,
    string Text,
    string ContentHash,
    string Language,
    string ExtractorId,
    string ExtractorVersion,
    string ChunkerId,
    string ChunkerVersion);

public sealed record ContentChunkingOptions(
    int TargetCharacters = 1600,
    int MaxCharacters = 2400,
    int OverlapUnits = 1)
{
    public static ContentChunkingOptions Default { get; } = new();

    public ContentChunkingOptions Normalize()
    {
        var target = Math.Clamp(TargetCharacters, 128, 64 * 1024);
        var max = Math.Clamp(MaxCharacters, target, 96 * 1024);
        var overlap = Math.Clamp(OverlapUnits, 0, 32);
        return new ContentChunkingOptions(target, max, overlap);
    }
}

public interface IContentChunker
{
    IReadOnlyList<ContentChunk> CreateChunks(
        IReadOnlyList<ContentUnit> units,
        ContentChunkingOptions? options = null);
}

public sealed class ContentUnitChunker : IContentChunker
{
    public const string ChunkerId = "content-unit-window";
    public static readonly string ChunkerVersion = "1";

    public IReadOnlyList<ContentChunk> CreateChunks(
        IReadOnlyList<ContentUnit> units,
        ContentChunkingOptions? options = null)
    {
        ArgumentNullException.ThrowIfNull(units);

        var normalized = (options ?? ContentChunkingOptions.Default).Normalize();
        var sourceUnits = units
            .Where(unit => !string.IsNullOrWhiteSpace(unit.Text))
            .ToArray();
        if (sourceUnits.Length == 0)
            return Array.Empty<ContentChunk>();

        var chunks = new List<ContentChunk>();
        var index = 0;
        while (index < sourceUnits.Length)
        {
            var start = index;
            var end = start;
            var textLength = 0;

            while (end < sourceUnits.Length)
            {
                var text = sourceUnits[end].Text.Trim();
                var separatorLength = textLength == 0 ? 0 : Environment.NewLine.Length;
                var nextLength = textLength + separatorLength + text.Length;
                if (end > start && nextLength > normalized.MaxCharacters)
                    break;

                textLength = nextLength;
                end++;

                if (textLength >= normalized.TargetCharacters)
                    break;
            }

            if (end == start)
                end++;

            var chunkUnits = sourceUnits[start..end];
            chunks.Add(CreateChunk(chunkUnits));

            if (end >= sourceUnits.Length)
                break;

            var overlap = Math.Min(normalized.OverlapUnits, Math.Max(0, chunkUnits.Length - 1));
            index = Math.Max(start + 1, end - overlap);
        }

        return chunks;
    }

    private static ContentChunk CreateChunk(ContentUnit[] units)
    {
        var text = string.Join(Environment.NewLine, units.Select(unit => unit.Text.Trim()));
        var hash = ComputeContentHash(units, text);
        return new ContentChunk(
            CreateChunkKey(units, hash),
            units[0].FileId,
            units.Select(unit => unit.Id).ToArray(),
            MergeLocators(units),
            text,
            hash,
            SameString(units, unit => unit.Language) ?? string.Empty,
            SameString(units, unit => unit.ExtractorId) ?? string.Empty,
            SameString(units, unit => unit.ExtractorVersion) ?? string.Empty,
            ChunkerId,
            ChunkerVersion);
    }

    private static string CreateChunkKey(ContentUnit[] units, string hash) =>
        $"{units[0].FileId}:{units[0].Id}-{units[^1].Id}:{hash[..16]}";

    private static string ComputeContentHash(ContentUnit[] units, string text)
    {
        var builder = new StringBuilder();
        builder.Append(ChunkerId).Append('\n');
        builder.Append(ChunkerVersion).Append('\n');
        builder.Append(units[0].FileId).Append('\n');
        foreach (var unit in units)
        {
            builder.Append(unit.Id).Append(':').Append(unit.ContentHash).Append('\n');
        }

        builder.Append(text);
        var bytes = SHA256.HashData(Encoding.UTF8.GetBytes(builder.ToString()));
        return Convert.ToHexString(bytes).ToLowerInvariant();
    }

    private static SourceLocator MergeLocators(IReadOnlyList<ContentUnit> units)
    {
        var locators = units.Select(unit => unit.Locator).ToArray();
        var startLine = MinPositive(locators, locator => locator.StartLine);
        var endLine = MaxPositive(locators, locator => locator.EndLine ?? locator.StartLine);
        var page = SamePositive(locators, locator => locator.Page);
        var slide = SamePositive(locators, locator => locator.Slide);
        var sourceWidth = SamePositive(locators, locator => locator.SourceWidth);
        var sourceHeight = SamePositive(locators, locator => locator.SourceHeight);
        var region = MergeRegion(locators, sourceWidth, sourceHeight);

        return new SourceLocator(
            Page: page,
            Slide: slide,
            Sheet: SameString(locators, locator => locator.Sheet),
            CellRange: SameString(locators, locator => locator.CellRange),
            StartLine: startLine,
            EndLine: endLine,
            Column: startLine == endLine ? SamePositive(locators, locator => locator.Column) : null,
            Section: SameString(locators, locator => locator.Section),
            ArchiveMember: SameString(locators, locator => locator.ArchiveMember),
            DisplayText: CreateDisplayText(locators, page, slide, startLine, endLine),
            X: region?.X,
            Y: region?.Y,
            Width: region?.Width,
            Height: region?.Height,
            SourceWidth: sourceWidth,
            SourceHeight: sourceHeight);
    }

    private static string? CreateDisplayText(
        IReadOnlyList<SourceLocator> locators,
        int? page,
        int? slide,
        int? startLine,
        int? endLine)
    {
        if (page is not null && startLine is not null)
            return startLine == endLine ? $"page {page} line {startLine}" : $"page {page} lines {startLine}-{endLine}";
        if (page is not null)
            return $"page {page}";
        if (slide is not null)
            return $"slide {slide}";
        if (startLine is not null)
            return startLine == endLine ? $"line {startLine}" : $"lines {startLine}-{endLine}";

        var archiveMember = SameString(locators, locator => locator.ArchiveMember);
        if (!string.IsNullOrWhiteSpace(archiveMember))
            return archiveMember;

        var display = locators
            .Select(locator => locator.DisplayText)
            .FirstOrDefault(value => !string.IsNullOrWhiteSpace(value));
        return display;
    }

    private static (int X, int Y, int Width, int Height)? MergeRegion(
        IReadOnlyList<SourceLocator> locators,
        int? sourceWidth,
        int? sourceHeight)
    {
        if (sourceWidth is null || sourceHeight is null)
            return null;

        var regions = new List<(int X, int Y, int Right, int Bottom)>();
        foreach (var locator in locators)
        {
            if (locator.X is not { } x ||
                locator.Y is not { } y ||
                locator.Width is not { } width ||
                locator.Height is not { } height)
            {
                return null;
            }

            regions.Add((x, y, x + width, y + height));
        }

        if (regions.Count == 0)
            return null;

        var left = regions.Min(region => region.X);
        var top = regions.Min(region => region.Y);
        var right = regions.Max(region => region.Right);
        var bottom = regions.Max(region => region.Bottom);
        return (left, top, Math.Max(1, right - left), Math.Max(1, bottom - top));
    }

    private static int? SamePositive(IReadOnlyList<SourceLocator> locators, Func<SourceLocator, int?> select)
    {
        var values = locators
            .Select(select)
            .Where(value => value is > 0)
            .Select(value => value!.Value)
            .Distinct()
            .Take(2)
            .ToArray();
        return values.Length == 1 ? values[0] : null;
    }

    private static int? MinPositive(IReadOnlyList<SourceLocator> locators, Func<SourceLocator, int?> select)
    {
        int? min = null;
        foreach (var value in locators.Select(select).Where(value => value is > 0).Select(value => value!.Value))
            min = min is null ? value : Math.Min(min.Value, value);
        return min;
    }

    private static int? MaxPositive(IReadOnlyList<SourceLocator> locators, Func<SourceLocator, int?> select)
    {
        int? max = null;
        foreach (var value in locators.Select(select).Where(value => value is > 0).Select(value => value!.Value))
            max = max is null ? value : Math.Max(max.Value, value);
        return max;
    }

    private static string? SameString<T>(IReadOnlyList<T> items, Func<T, string?> select)
    {
        var values = items
            .Select(select)
            .Where(value => !string.IsNullOrWhiteSpace(value))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .Take(2)
            .ToArray();
        return values.Length == 1 ? values[0] : null;
    }
}
