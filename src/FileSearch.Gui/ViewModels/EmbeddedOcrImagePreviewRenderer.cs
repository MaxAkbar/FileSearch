using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using FileSearch.Core.Extractors;

namespace FileSearch.Gui.ViewModels;

internal static class EmbeddedOcrImagePreviewRenderer
{
    private const int MaxEmailPreviewChars = 10 * 1024 * 1024;

    private static readonly TimeSpan s_regexTimeout = TimeSpan.FromSeconds(2);
    private static readonly Regex s_boundaryRegex = new(@"boundary=(?:(""[^""\r\n]+"")|([^;\r\n]+))", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant, s_regexTimeout);
    private static readonly Regex s_headerFoldRegex = new("\n[\t ]+", RegexOptions.CultureInvariant, s_regexTimeout);
    private static readonly Regex s_encodedWordRegex = new(@"=\?([^?]+)\?([bqBQ])\?([^?]+)\?=", RegexOptions.CultureInvariant, s_regexTimeout);
    private static readonly Regex s_filenameRegex = new(@"(?:filename|name)=""?([^"";\r\n]+)""?", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant, s_regexTimeout);

    public static async Task<string?> ExtractAsync(
        string parentPath,
        SourceAnchor anchor,
        CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(parentPath) ||
            string.IsNullOrWhiteSpace(anchor.MemberPath) ||
            !File.Exists(parentPath))
        {
            return null;
        }

        try
        {
            return anchor.Kind == SourceAnchorKind.Email
                ? await ExtractEmailImageAsync(parentPath, anchor, cancellationToken).ConfigureAwait(false)
                : await ExtractZipMemberAsync(parentPath, anchor, cancellationToken).ConfigureAwait(false);
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

    private static async Task<string?> ExtractZipMemberAsync(
        string parentPath,
        SourceAnchor anchor,
        CancellationToken cancellationToken)
    {
        var cachePath = GetCachePath(parentPath, anchor);
        if (File.Exists(cachePath))
            return cachePath;

        cancellationToken.ThrowIfCancellationRequested();
        using var stream = new FileStream(parentPath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
        using var archive = new ZipArchive(stream, ZipArchiveMode.Read);
        var entry = FindEntry(archive, anchor.MemberPath!);
        if (entry is null)
            return null;

        var bytes = await ReadEntryBytesAsync(entry, cancellationToken).ConfigureAwait(false);
        if (bytes.Length == 0)
            return null;

        return await WriteCacheAsync(cachePath, bytes, cancellationToken).ConfigureAwait(false);
    }

    private static ZipArchiveEntry? FindEntry(ZipArchive archive, string memberPath)
    {
        var normalized = NormalizeMemberPath(memberPath);
        if (string.IsNullOrEmpty(normalized))
            return null;

        var entries = archive.Entries
            .Where(entry => entry.Length > 0 && !string.IsNullOrEmpty(entry.Name))
            .ToList();

        var exact = entries.FirstOrDefault(entry =>
            string.Equals(NormalizeMemberPath(entry.FullName), normalized, StringComparison.OrdinalIgnoreCase));
        if (exact is not null)
            return exact;

        var suffix = "/" + normalized;
        return entries
            .Where(entry => NormalizeMemberPath(entry.FullName).EndsWith(suffix, StringComparison.OrdinalIgnoreCase))
            .OrderBy(entry => entry.FullName.Length)
            .FirstOrDefault();
    }

    private static async Task<string?> ExtractEmailImageAsync(
        string parentPath,
        SourceAnchor anchor,
        CancellationToken cancellationToken)
    {
        var cachePath = GetCachePath(parentPath, anchor);
        if (File.Exists(cachePath))
            return cachePath;

        var content = await ReadPreviewTextAsync(parentPath, cancellationToken).ConfigureAwait(false);
        foreach (var part in ExtractEmailImageParts(content))
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (!MemberPathMatches(part.MemberPath, anchor.MemberPath!))
                continue;

            return await WriteCacheAsync(cachePath, part.Bytes, cancellationToken).ConfigureAwait(false);
        }

        return null;
    }

    private static async Task<string> ReadPreviewTextAsync(string path, CancellationToken cancellationToken)
    {
        using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
        using var reader = new StreamReader(stream, Encoding.UTF8, detectEncodingFromByteOrderMarks: true);
        var buffer = new char[MaxEmailPreviewChars];
        var read = 0;
        while (read < buffer.Length)
        {
            var count = await reader.ReadAsync(buffer.AsMemory(read, buffer.Length - read), cancellationToken).ConfigureAwait(false);
            if (count == 0)
                break;

            read += count;
        }

        return new string(buffer, 0, read);
    }

    private static IEnumerable<EmailImagePart> ExtractEmailImageParts(string content)
    {
        var (headers, body) = SplitHeadersAndBody(content);
        var boundary = s_boundaryRegex.Match(headers);
        if (!boundary.Success)
            yield break;

        var boundaryValue = boundary.Groups[1].Success
            ? boundary.Groups[1].Value.Trim('"')
            : boundary.Groups[2].Value.Trim();
        var delimiter = "--" + boundaryValue;
        int unnamedIndex = 0;

        foreach (var section in body.Split(delimiter, StringSplitOptions.RemoveEmptyEntries))
        {
            if (section.StartsWith("--", StringComparison.Ordinal))
                continue;

            var (partHeaders, partBody) = SplitHeadersAndBody(section.TrimStart('\r', '\n'));
            if (!IsEmailImagePart(partHeaders))
                continue;

            var memberPath = GetEmailImagePartName(partHeaders, ++unnamedIndex);
            byte[] bytes;
            try
            {
                bytes = DecodeEmailPartBytes(partBody, partHeaders);
            }
            catch
            {
                continue;
            }

            if (bytes.Length > 0)
                yield return new EmailImagePart(memberPath, bytes);
        }
    }

    private static (string Headers, string Body) SplitHeadersAndBody(string content)
    {
        var normalized = content
            .Replace("\r\n", "\n", StringComparison.Ordinal)
            .TrimStart('\uFEFF', '\r', '\n');
        var separator = normalized.IndexOf("\n\n", StringComparison.Ordinal);
        return separator < 0
            ? (normalized, string.Empty)
            : (normalized[..separator], normalized[(separator + 2)..]);
    }

    private static bool IsEmailImagePart(string headers) =>
        headers.Contains("Content-Type: image/", StringComparison.OrdinalIgnoreCase) ||
        (headers.Contains("Content-Disposition: attachment", StringComparison.OrdinalIgnoreCase) &&
         IsImageName(GetEmailImagePartName(headers, 0)));

    private static string GetEmailImagePartName(string headers, int fallbackIndex)
    {
        var unfolded = s_headerFoldRegex.Replace(headers, " ");
        var match = s_filenameRegex.Match(unfolded);
        if (match.Success && !string.IsNullOrWhiteSpace(match.Groups[1].Value))
            return DecodeEmailHeader(match.Groups[1].Value.Trim());

        return fallbackIndex > 0
            ? $"image-{fallbackIndex}.bin"
            : string.Empty;
    }

    private static bool IsImageName(string name)
    {
        var extension = Path.GetExtension(name);
        return s_imageExtensions.Contains(extension);
    }

    private static byte[] DecodeEmailPartBytes(string body, string headers)
    {
        if (headers.Contains("Content-Transfer-Encoding: base64", StringComparison.OrdinalIgnoreCase))
        {
            var compact = new string(body.Where(ch => !char.IsWhiteSpace(ch)).ToArray());
            return Convert.FromBase64String(compact);
        }

        return headers.Contains("Content-Transfer-Encoding: quoted-printable", StringComparison.OrdinalIgnoreCase)
            ? DecodeQuotedPrintableBytes(body)
            : Encoding.ASCII.GetBytes(body);
    }

    private static string DecodeEmailHeader(string value) =>
        s_encodedWordRegex.Replace(value, match =>
        {
            var charset = match.Groups[1].Value;
            var encodingKind = match.Groups[2].Value;
            var encoded = match.Groups[3].Value;

            try
            {
                var bytes = encodingKind.Equals("B", StringComparison.OrdinalIgnoreCase)
                    ? Convert.FromBase64String(encoded)
                    : DecodeQuotedPrintableBytes(encoded.Replace('_', ' '));
                return Encoding.GetEncoding(charset).GetString(bytes);
            }
            catch
            {
                return match.Value;
            }
        });

    private static byte[] DecodeQuotedPrintableBytes(string value)
    {
        var withoutSoftBreaks = value
            .Replace("=\r\n", string.Empty, StringComparison.Ordinal)
            .Replace("=\n", string.Empty, StringComparison.Ordinal);

        var bytes = new List<byte>(withoutSoftBreaks.Length);
        for (int i = 0; i < withoutSoftBreaks.Length; i++)
        {
            var ch = withoutSoftBreaks[i];
            if (ch == '=' &&
                i + 2 < withoutSoftBreaks.Length &&
                Uri.IsHexDigit(withoutSoftBreaks[i + 1]) &&
                Uri.IsHexDigit(withoutSoftBreaks[i + 2]))
            {
                bytes.Add(Convert.ToByte(withoutSoftBreaks.Substring(i + 1, 2), 16));
                i += 2;
            }
            else
            {
                bytes.Add((byte)ch);
            }
        }

        return bytes.ToArray();
    }

    private static async Task<byte[]> ReadEntryBytesAsync(
        ZipArchiveEntry entry,
        CancellationToken cancellationToken)
    {
        using var stream = entry.Open();
        using var memory = new MemoryStream();
        await stream.CopyToAsync(memory, cancellationToken).ConfigureAwait(false);
        return memory.ToArray();
    }

    private static async Task<string> WriteCacheAsync(
        string cachePath,
        byte[] bytes,
        CancellationToken cancellationToken)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(cachePath)!);
        var tempPath = cachePath + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try
        {
            await File.WriteAllBytesAsync(tempPath, bytes, cancellationToken).ConfigureAwait(false);
            File.Move(tempPath, cachePath, overwrite: true);
        }
        finally
        {
            TryDelete(tempPath);
        }

        return cachePath;
    }

    private static string GetCachePath(string parentPath, SourceAnchor anchor)
    {
        var memberPath = anchor.MemberPath ?? string.Empty;
        var file = new FileInfo(parentPath);
        var input = string.Join(
            "|",
            Path.GetFullPath(parentPath),
            file.LastWriteTimeUtc.Ticks.ToString(System.Globalization.CultureInfo.InvariantCulture),
            file.Length.ToString(System.Globalization.CultureInfo.InvariantCulture),
            anchor.Kind.ToString(),
            NormalizeMemberPath(memberPath),
            anchor.SourceWidth?.ToString(System.Globalization.CultureInfo.InvariantCulture) ?? string.Empty,
            anchor.SourceHeight?.ToString(System.Globalization.CultureInfo.InvariantCulture) ?? string.Empty);
        var hash = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(input)));
        return Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "FileSearch",
            "PreviewCache",
            $"embedded-{hash}{GetCacheExtension(memberPath)}");
    }

    private static string GetCacheExtension(string memberPath)
    {
        var extension = Path.GetExtension(memberPath);
        return s_imageExtensions.Contains(extension)
            ? extension.ToLowerInvariant()
            : ".png";
    }

    private static bool MemberPathMatches(string actual, string expected)
    {
        var normalizedActual = NormalizeMemberPath(actual);
        var normalizedExpected = NormalizeMemberPath(expected);
        return string.Equals(normalizedActual, normalizedExpected, StringComparison.OrdinalIgnoreCase) ||
            string.Equals(GetLastPathSegment(normalizedActual), GetLastPathSegment(normalizedExpected), StringComparison.OrdinalIgnoreCase);
    }

    private static string NormalizeMemberPath(string value) =>
        value.Replace('\\', '/').TrimStart('/').Trim();

    private static string GetLastPathSegment(string value)
    {
        var slash = value.LastIndexOf('/');
        return slash >= 0 ? value[(slash + 1)..] : value;
    }

    private static void TryDelete(string path)
    {
        try
        {
            if (File.Exists(path))
                File.Delete(path);
        }
        catch
        {
            // Best-effort cleanup for an abandoned temp cache file.
        }
    }

    private static readonly HashSet<string> s_imageExtensions = new(StringComparer.OrdinalIgnoreCase)
    {
        ".png",
        ".jpg",
        ".jpeg",
        ".bmp",
        ".tif",
        ".tiff",
    };

    private sealed record EmailImagePart(string MemberPath, byte[] Bytes);
}
