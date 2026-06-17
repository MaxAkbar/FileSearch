using System;
using System.Collections.Generic;
using System.Runtime.CompilerServices;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;

namespace FileSearch.Core.Extractors;

/// <summary>
/// Extracts common headers and body text from .eml email messages.
/// </summary>
public sealed class EmlExtractor : ITextExtractor
{
    public string ExtractorId => "filesearch.eml";

    public string ExtractorVersion => "1";

    /// <summary>Cap on how much of a message is read; the rest isn't searched.</summary>
    private const int MaxContentChars = 10 * 1024 * 1024;

    // Time-boxed like RegexQuery — these run over untrusted message content.
    private static readonly TimeSpan s_regexTimeout = TimeSpan.FromSeconds(2);

    private static readonly Regex s_boundaryRegex = new(@"boundary=(?:(""[^""\r\n]+"")|([^;\r\n]+))", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant, s_regexTimeout);
    private static readonly Regex s_headerFoldRegex = new("\n[\t ]+", RegexOptions.CultureInvariant, s_regexTimeout);
    private static readonly Regex s_encodedWordRegex = new(@"=\?([^?]+)\?([bqBQ])\?([^?]+)\?=", RegexOptions.CultureInvariant, s_regexTimeout);
    private static readonly Regex s_charsetRegex = new(@"charset=""?([^"";\s]+)""?", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant, s_regexTimeout);

    public IReadOnlyCollection<string> SupportedExtensions { get; } = new[] { ".eml" };

    public async IAsyncEnumerable<TextLine> ExtractAsync(
        string path,
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
        var normalized = content.Replace("\r\n", "\n", StringComparison.Ordinal);
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
}
