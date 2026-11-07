using System.IO;
using FileSearch.Core.Engine;

namespace FileSearch.Gui.Services;

public static class ArchivePreviewService
{
    public static Task<DocumentPreview> LoadAsync(string path, Hit? hit, string? selected, CancellationToken token) => Task.Run(() =>
    {
        using var archive = PreviewPackage.Open(path);
        token.ThrowIfCancellationRequested();
        var members = archive.Entries.Where(entry => entry.Name.Length > 0 && PreviewPackage.IsSafeMember(entry.FullName))
            .Select(entry => new PreviewPart(entry.FullName, entry.FullName)).ToArray();
        string? preferred = selected ?? hit?.Snippet?.Locator?.ArchiveMember ?? hit?.Locator?.ArchiveMember ?? hit?.Anchor?.MemberPath;
        if (preferred is null && hit?.LineContent.StartsWith('[') == true && hit.LineContent.IndexOf(']') is var end && end > 1) preferred = hit.LineContent[1..end];
        string? chosen = members.FirstOrDefault(member => member.Id == preferred)?.Id ?? members.FirstOrDefault()?.Id;
        return new DocumentPreview(DocumentPreviewKind.Archive, $"ZIP · {members.Length:n0} files · Select a member to preview", Parts: members, SelectedPart: chosen);
    }, token);

    public static Task<ArchivePreviewLease> OpenMemberAsync(string path, string member, CancellationToken token) => Task.Run(() =>
    {
        if (!PreviewPackage.IsSafeMember(member)) throw new InvalidDataException("Unsafe archive member.");
        using var archive = PreviewPackage.Open(path);
        var entry = archive.GetEntry(member) ?? throw new InvalidDataException("The archive member no longer exists.");
        var bytes = PreviewPackage.Read(entry, token: token);
        token.ThrowIfCancellationRequested();
        // Entry names never form filesystem paths. Only a whitelisted-length extension is retained.
        string extension = Path.GetExtension(member);
        if (extension.Length > 16 || extension.Any(character => !char.IsAsciiLetterOrDigit(character) && character != '.')) extension = ".bin";
        string directory = Path.Combine(Path.GetTempPath(), "FileSearchPreview", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        var lease = new ArchivePreviewLease(Path.Combine(directory, "member" + extension));
        try
        {
            File.WriteAllBytes(lease.Path, bytes);
            token.ThrowIfCancellationRequested();
            return lease;
        }
        catch { lease.Dispose(); throw; }
    }, token);
}

/// <summary>Owns exactly one generated preview file and its otherwise empty directory.</summary>
public sealed class ArchivePreviewLease : IDisposable
{
    internal ArchivePreviewLease(string path) => Path = path;
    public string Path { get; }
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
