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
    private static readonly Regex s_boundaryRegex = new(@"boundary=(?:(""[^""\r\n]+"")|([^;\r\n]+))", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
    private static readonly Regex s_headerFoldRegex = new("\n[\t ]+", RegexOptions.CultureInvariant);
    private static readonly Regex s_encodedWordRegex = new(@"=\?([^?]+)\?([bqBQ])\?([^?]+)\?=", RegexOptions.CultureInvariant);
    private static readonly Regex s_quotedPrintableHexRegex = new("=([0-9A-Fa-f]{2})", RegexOptions.CultureInvariant);

    public IReadOnlyCollection<string> SupportedExtensions { get; } = new[] { ".eml" };

    public async IAsyncEnumerable<TextLine> ExtractAsync(
        string path,
        [EnumeratorCancellation] CancellationToken cancellationToken)
    {
        var content = await File.ReadAllTextAsync(path, cancellationToken).ConfigureAwait(false);
        var lines = ExtractMessageText(content);

        int lineNumber = 0;
        foreach (var line in lines)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var normalized = MarkupText.Normalize(line);
            if (string.IsNullOrEmpty(normalized)) continue;

            lineNumber++;
            yield return new TextLine(lineNumber, normalized);
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
        var normalized = content.Replace("\r\n", "\n");
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
            ? DecodeQuotedPrintable(body)
            : body;

        return headers.Contains("Content-Type: text/html", StringComparison.OrdinalIgnoreCase)
            ? MarkupText.FromHtml(decoded)
            : decoded;
    }

    private static string DecodeHeader(string value)
    {
        return s_encodedWordRegex.Replace(value, match =>
        {
            var charset = match.Groups[1].Value;
            var encoding = match.Groups[2].Value;
            var encoded = match.Groups[3].Value;

            try
            {
                var bytes = encoding.Equals("B", StringComparison.OrdinalIgnoreCase)
                    ? Convert.FromBase64String(encoded)
                    : Encoding.ASCII.GetBytes(DecodeQuotedPrintable(encoded.Replace('_', ' ')));
                return Encoding.GetEncoding(charset).GetString(bytes);
            }
            catch
            {
                return match.Value;
            }
        });
    }

    private static string DecodeQuotedPrintable(string value)
    {
        var softLineBreaksRemoved = value.Replace("=\r\n", string.Empty).Replace("=\n", string.Empty);
        return s_quotedPrintableHexRegex.Replace(softLineBreaksRemoved, match =>
            ((char)Convert.ToByte(match.Groups[1].Value, 16)).ToString());
    }
}
