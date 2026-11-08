using System.Globalization;
using System.IO;
using FileSearch.Core.Engine;
using FileSearch.Core.Extractors;

namespace FileSearch.Gui.Services;

public static class ArchivePreviewService
{
    public static Task<DocumentPreview> LoadAsync(string path, Hit? hit, string? selected, CancellationToken token) => Task.Run(() =>
    {
        var fingerprint = GetFingerprint(path);
        ValidateFingerprint(path, ExpectedFingerprint(hit));
        using var archive = PreviewPackage.Open(path);
        token.ThrowIfCancellationRequested();
        var duplicates = archive.Entries.GroupBy(entry => entry.FullName, StringComparer.Ordinal)
            .Where(group => group.Count() > 1).Select(group => group.Key).ToHashSet(StringComparer.Ordinal);
        var members = archive.Entries.Where(entry => entry.Name.Length > 0 && PreviewPackage.IsSafeMember(entry.FullName) && !duplicates.Contains(entry.FullName))
            .Select(entry => new PreviewPart(entry.FullName, entry.FullName)).ToArray();
        string? preferred = selected ?? hit?.Snippet?.Locator?.ArchiveMember ?? hit?.Locator?.ArchiveMember ?? hit?.Anchor?.MemberPath;
        if (preferred is null && hit?.LineContent.StartsWith('[') == true && hit.LineContent.IndexOf(']') is var end && end > 1) preferred = hit.LineContent[1..end];
        if (preferred is not null && !members.Any(member => member.Id == preferred))
            throw new InvalidDataException(duplicates.Contains(preferred)
                ? "The archive member name is ambiguous. Duplicate names cannot be previewed."
                : "The selected archive member is unavailable. Refresh the search before previewing it.");
        string? chosen = preferred ?? members.FirstOrDefault()?.Id;
        ValidateFingerprint(path, fingerprint);
        string skipped = duplicates.Count > 0 ? $" · {duplicates.Count:n0} ambiguous member names skipped" : "";
        return new DocumentPreview(DocumentPreviewKind.Archive, $"ZIP · {members.Length:n0} files{skipped} · Select a member to preview", Parts: members, SelectedPart: chosen);
    }, token);

    public static Task<ArchivePreviewLease> OpenMemberAsync(string path, string member, CancellationToken token,
        string? expectedFingerprint = null) => Task.Run(() =>
    {
        if (!PreviewPackage.IsSafeMember(member)) throw new InvalidDataException("Unsafe archive member.");
        var fingerprint = GetFingerprint(path);
        ValidateFingerprint(path, expectedFingerprint);
        using var archive = PreviewPackage.Open(path);
        var entries = archive.Entries.Where(candidate => string.Equals(candidate.FullName, member, StringComparison.Ordinal)).ToArray();
        if (entries.Length != 1) throw new InvalidDataException(entries.Length == 0
            ? "The archive member no longer exists."
            : "The archive member name is ambiguous. Duplicate names cannot be previewed.");
        var entry = entries[0];
        var bytes = PreviewPackage.Read(entry, token: token);
        token.ThrowIfCancellationRequested();
        ValidateFingerprint(path, fingerprint);
        // Entry names never form filesystem paths. Only a whitelisted-length extension is retained.
        string extension = Path.GetExtension(member);
        if (extension.Length > 16 || extension.Any(character => !char.IsAsciiLetterOrDigit(character) && character != '.')) extension = ".bin";
        string directory = Path.Combine(Path.GetTempPath(), "FileSearchPreview", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        var lease = new ArchivePreviewLease(Path.Combine(directory, "member" + extension), fingerprint);
        try
        {
            File.WriteAllBytes(lease.Path, bytes);
            token.ThrowIfCancellationRequested();
            return lease;
        }
        catch { lease.Dispose(); throw; }
    }, token);

    internal static string GetFingerprint(string path)
    {
        var info = new FileInfo(path);
        return string.Create(CultureInfo.InvariantCulture, $"{info.Length}:{info.LastWriteTimeUtc.Ticks}");
    }

    internal static string? ExpectedFingerprint(Hit? hit) =>
        hit?.Snippet?.Locator?.ArchiveFingerprint ?? hit?.Locator?.ArchiveFingerprint ?? hit?.Anchor?.ArchiveFingerprint;

    internal static bool HasMemberHits(string path, IReadOnlyList<Hit> hits) =>
        Path.GetExtension(path).Equals(".zip", StringComparison.OrdinalIgnoreCase) && hits.Any(hit =>
            hit.Kind == HitKind.Content && (hit.Anchor?.Kind == SourceAnchorKind.Archive ||
                (hit.Snippet?.Locator?.ArchiveMember ?? hit.Locator?.ArchiveMember) is not null));

    private static void ValidateFingerprint(string path, string? expected)
    {
        if (expected is not null && !string.Equals(expected, GetFingerprint(path), StringComparison.Ordinal))
            throw new InvalidDataException("The ZIP changed. Refresh the index or search again before opening this member.");
    }

    internal static Hit? CreateMemberHit(string member, string extractedPath, Hit? hit)
    {
        if (hit is null) return null;
        var source = hit.Snippet?.Locator ?? hit.Locator;
        string? matchedMember = source?.ArchiveMember ?? hit.Anchor?.MemberPath;
        if (!string.Equals(member, matchedMember, StringComparison.Ordinal)) return null;
        var inner = source?.ArchiveContentAnchor ?? hit.Anchor?.ArchiveContentAnchor;
        if (inner is null && hit.Anchor is { Kind: SourceAnchorKind.Archive, Line: null } &&
            string.Equals(hit.LineContent, $"=== {member} ===", StringComparison.Ordinal))
            return null; // A member-name marker has no matching line in the member's contents.
        int localLine = hit.Anchor?.Line ?? source?.StartLine ?? inner?.Line ?? 1;
        inner ??= new SourceAnchor(SourceAnchorKind.Text, $"line {localLine}", Line: localLine);
        var locator = SourceLocator.FromAnchor(inner, localLine);
        return hit with
        {
            Path = extractedPath,
            LineNumber = localLine,
            Anchor = inner,
            Locator = locator,
            Snippet = hit.Snippet is { } snippet ? snippet with { Locator = locator } : null,
            SizeBytes = new FileInfo(extractedPath).Length,
            ModifiedUtc = File.GetLastWriteTimeUtc(extractedPath),
        };
    }
}

/// <summary>Owns exactly one generated preview file and its otherwise empty directory.</summary>
public sealed class ArchivePreviewLease : IDisposable
{
    internal ArchivePreviewLease(string path, string? sourceFingerprint = null) { Path = path; SourceFingerprint = sourceFingerprint; }
    public string Path { get; }
    public string? SourceFingerprint { get; }
    private int _disposed;
    public void Dispose()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0) return;
        try { File.Delete(Path); Directory.Delete(System.IO.Path.GetDirectoryName(Path)!); }
        catch (IOException) { _ = RetryDeleteAsync(); }
        catch (UnauthorizedAccessException) { _ = RetryDeleteAsync(); }
    }
    private async Task RetryDeleteAsync()
    {
        for (int attempt = 0; attempt < 10; attempt++)
        {
            await Task.Delay(500).ConfigureAwait(false);
            try { File.Delete(Path); Directory.Delete(System.IO.Path.GetDirectoryName(Path)!); return; }
            catch (IOException) { }
            catch (UnauthorizedAccessException) { }
        }
    }
}
