using System;
using System.Collections.Generic;
using System.Linq;
using System.Runtime.CompilerServices;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;

namespace FileSearch.Core.Extractors;

/// <summary>
/// Extracts common headers and body text from .eml email messages.
/// </summary>
public sealed class EmlExtractor : IContextualTextExtractor
{
    private readonly IEmbeddedImageOcrService _embeddedImageOcr;

    public EmlExtractor(IEmbeddedImageOcrService? embeddedImageOcr = null)
    {
        _embeddedImageOcr = embeddedImageOcr ?? new NullEmbeddedImageOcrService();
    }

    public string ExtractorId => "filesearch.eml";

    public string ExtractorVersion => "2";

    /// <summary>Cap on how much of a message is read; the rest isn't searched.</summary>
    private const int MaxContentChars = 10 * 1024 * 1024;

    // Time-boxed like RegexQuery — these run over untrusted message content.
    private static readonly TimeSpan s_regexTimeout = TimeSpan.FromSeconds(2);

    private static readonly Regex s_boundaryRegex = new(@"boundary=(?:(""[^""\r\n]+"")|([^;\r\n]+))", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant, s_regexTimeout);
    private static readonly Regex s_headerFoldRegex = new("\n[\t ]+", RegexOptions.CultureInvariant, s_regexTimeout);
    private static readonly Regex s_encodedWordRegex = new(@"=\?([^?]+)\?([bqBQ])\?([^?]+)\?=", RegexOptions.CultureInvariant, s_regexTimeout);
    private static readonly Regex s_charsetRegex = new(@"charset=""?([^"";\s]+)""?", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant, s_regexTimeout);
    private static readonly Regex s_filenameRegex = new(@"(?:filename|name)=""?([^"";\r\n]+)""?", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant, s_regexTimeout);

    public IReadOnlyCollection<string> SupportedExtensions { get; } = new[] { ".eml" };

    public async IAsyncEnumerable<TextLine> ExtractAsync(
        string path,
        [EnumeratorCancellation] CancellationToken cancellationToken)
    {
        await foreach (var line in ExtractAsync(path, new TextExtractionContext(), cancellationToken).ConfigureAwait(false))
            yield return line;
    }

    public async IAsyncEnumerable<TextLine> ExtractAsync(
        string path,
        TextExtractionContext context,
        [EnumeratorCancellation] CancellationToken cancellationToken)
    {
        var content = await BoundedFileReader.ReadTextAsync(path, MaxContentChars, cancellationToken).ConfigureAwait(false);

        int lineNumber = 0;
        foreach (var block in ExtractMessageText(content))
        {
            // Split before normalizing — Normalize collapses newlines, which
            // used to flatten an entire body into one giant line.
            foreach (var raw in block.Split('\n'))
            {
                cancellationToken.ThrowIfCancellationRequested();
                var normalized = MarkupText.Normalize(raw);
                if (string.IsNullOrEmpty(normalized)) continue;

                lineNumber++;
                yield return new TextLine(lineNumber, normalized);
            }
        }

        if (!context.EnableOcr)
            yield break;

        foreach (var imagePart in ExtractImageParts(content))
        {
            cancellationToken.ThrowIfCancellationRequested();
            var request = new EmbeddedImageOcrRequest(
                SourceAnchorKind.Email,
                imagePart.MemberPath,
                $"email image {imagePart.MemberPath}",
                Section: imagePart.MemberPath);
            await foreach (var line in _embeddedImageOcr.ExtractAsync(imagePart.Bytes, request, cancellationToken).ConfigureAwait(false))
                yield return line with { Number = ++lineNumber };
        }
    }

    private static IEnumerable<string> ExtractMessageText(string content)
    {
        var (headers, body) = SplitHeadersAndBody(content);
        foreach (var header in ExtractUsefulHeaders(headers))
            yield return DecodeHeader(header);

        var boundary = s_boundaryRegex.Match(headers);
        if (boundary.Success)
        {
            var boundaryValue = boundary.Groups[1].Success
                ? boundary.Groups[1].Value.Trim('"')
                : boundary.Groups[2].Value.Trim();
            foreach (var part in ExtractMultipartBody(body, boundaryValue))
                yield return part;
        }
        else
        {
            yield return DecodeBody(body, headers);
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

    private static IEnumerable<string> ExtractUsefulHeaders(string headers)
    {
        var unfolded = s_headerFoldRegex.Replace(headers, " ");
        foreach (var line in unfolded.Split('\n'))
        {
            if (line.StartsWith("Subject:", StringComparison.OrdinalIgnoreCase) ||
                line.StartsWith("From:", StringComparison.OrdinalIgnoreCase) ||
                line.StartsWith("To:", StringComparison.OrdinalIgnoreCase) ||
                line.StartsWith("Cc:", StringComparison.OrdinalIgnoreCase))
            {
                yield return line;
            }
        }
    }

    private static IEnumerable<string> ExtractMultipartBody(string body, string boundary)
    {
        var delimiter = "--" + boundary;
        foreach (var section in body.Split(delimiter, StringSplitOptions.RemoveEmptyEntries))
        {
            if (section.StartsWith("--", StringComparison.Ordinal)) continue;

            var (headers, partBody) = SplitHeadersAndBody(section.TrimStart('\r', '\n'));
            if (headers.Contains("Content-Disposition: attachment", StringComparison.OrdinalIgnoreCase)) continue;
            if (!headers.Contains("Content-Type: text/", StringComparison.OrdinalIgnoreCase)) continue;

            yield return DecodeBody(partBody, headers);
        }
    }

    private static IEnumerable<EmailImagePart> ExtractImageParts(string content)
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
            if (!IsImagePart(partHeaders))
                continue;

            var memberPath = GetImagePartName(partHeaders, ++unnamedIndex);
            byte[] bytes;
            try
            {
                bytes = DecodePartBytes(partBody, partHeaders);
            }
            catch
            {
                continue;
            }

            if (bytes.Length > 0)
                yield return new EmailImagePart(memberPath, bytes);
        }
    }

    private static bool IsImagePart(string headers) =>
        headers.Contains("Content-Type: image/", StringComparison.OrdinalIgnoreCase) ||
        (headers.Contains("Content-Disposition: attachment", StringComparison.OrdinalIgnoreCase) &&
         IsImageName(GetImagePartName(headers, 0)));

    private static string GetImagePartName(string headers, int fallbackIndex)
    {
        var match = s_filenameRegex.Match(headers);
        if (match.Success && !string.IsNullOrWhiteSpace(match.Groups[1].Value))
            return DecodeHeader(match.Groups[1].Value.Trim());

        return fallbackIndex > 0
            ? $"image-{fallbackIndex}.bin"
            : string.Empty;
    }

    private static bool IsImageName(string name)
    {
        var extension = Path.GetExtension(name);
        return extension.Equals(".png", StringComparison.OrdinalIgnoreCase) ||
            extension.Equals(".jpg", StringComparison.OrdinalIgnoreCase) ||
            extension.Equals(".jpeg", StringComparison.OrdinalIgnoreCase) ||
            extension.Equals(".bmp", StringComparison.OrdinalIgnoreCase) ||
            extension.Equals(".tif", StringComparison.OrdinalIgnoreCase) ||
            extension.Equals(".tiff", StringComparison.OrdinalIgnoreCase);
    }

    private static byte[] DecodePartBytes(string body, string headers)
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

    private static string DecodeBody(string body, string headers)
    {
        var decoded = headers.Contains("Content-Transfer-Encoding: quoted-printable", StringComparison.OrdinalIgnoreCase)
            ? GetCharset(headers).GetString(DecodeQuotedPrintableBytes(body))
            : body;

        return headers.Contains("Content-Type: text/html", StringComparison.OrdinalIgnoreCase)
            ? MarkupText.FromHtml(decoded)
            : decoded;
    }

    private static Encoding GetCharset(string headers)
    {
        var match = s_charsetRegex.Match(headers);
        if (!match.Success)
            return Encoding.UTF8;

        try
        {
            return Encoding.GetEncoding(match.Groups[1].Value);
        }
        catch (ArgumentException)
        {
            return Encoding.UTF8;
        }
    }

    private static string DecodeHeader(string value)
    {
        return s_encodedWordRegex.Replace(value, match =>
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
    }

    /// <summary>
    /// Decodes =XX escapes into raw bytes so multi-byte encodings survive —
    /// the old char-per-byte version mangled any non-ASCII text into
    /// unsearchable mojibake.
    /// </summary>
    private static byte[] DecodeQuotedPrintableBytes(string value)
    {
        var withoutSoftBreaks = value
            .Replace("=\r\n", string.Empty, StringComparison.Ordinal)
            .Replace("=\n", string.Empty, StringComparison.Ordinal);

        var bytes = new List<byte>(withoutSoftBreaks.Length);
        for (int i = 0; i < withoutSoftBreaks.Length; i++)
        {
            var ch = withoutSoftBreaks[i];
            if (ch == '=' && i + 2 < withoutSoftBreaks.Length &&
                Uri.IsHexDigit(withoutSoftBreaks[i + 1]) && Uri.IsHexDigit(withoutSoftBreaks[i + 2]))
            {
                bytes.Add(Convert.ToByte(withoutSoftBreaks.Substring(i + 1, 2), 16));
                i += 2;
            }
            else
            {
                // Quoted-printable text outside escapes is ASCII by spec.
                bytes.Add((byte)ch);
            }
        }

        return bytes.ToArray();
    }

    private sealed record EmailImagePart(string MemberPath, byte[] Bytes);
}
