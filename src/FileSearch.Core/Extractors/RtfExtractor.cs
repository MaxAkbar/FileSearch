using System.Collections.Generic;
using System.Globalization;
using System.Runtime.CompilerServices;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

namespace FileSearch.Core.Extractors;

/// <summary>
/// Extracts plain text from common RTF control syntax.
/// </summary>
public sealed class RtfExtractor : ITextExtractor
{
    public IReadOnlyCollection<string> SupportedExtensions { get; } = new[] { ".rtf" };

    public async IAsyncEnumerable<TextLine> ExtractAsync(
        string path,
        [EnumeratorCancellation] CancellationToken cancellationToken)
    {
        var content = await File.ReadAllTextAsync(path, cancellationToken).ConfigureAwait(false);
        var text = MarkupText.Normalize(StripRtf(content));
        if (!string.IsNullOrEmpty(text))
            yield return new TextLine(1, text);
    }

    private static string StripRtf(string value)
    {
        var sb = new StringBuilder(value.Length);
        int ignorableDepth = 0;
        int depth = 0;

        for (int i = 0; i < value.Length; i++)
        {
            var ch = value[i];

            if (ch == '{')
            {
                depth++;
                if (i + 1 < value.Length && value[i + 1] == '\\' && i + 2 < value.Length && value[i + 2] == '*')
                    ignorableDepth = depth;
                continue;
            }

            if (ch == '}')
            {
                if (ignorableDepth == depth)
                    ignorableDepth = 0;
                depth = Math.Max(0, depth - 1);
                continue;
            }

            if (ignorableDepth > 0) continue;

            if (ch != '\\')
            {
                sb.Append(ch);
                continue;
            }

            if (++i >= value.Length) break;
            ch = value[i];

            if (ch is '\\' or '{' or '}')
            {
                sb.Append(ch);
                continue;
            }

            if (ch == '\'')
            {
                if (i + 2 < value.Length)
                {
                    var hex = value.Substring(i + 1, 2);
                    if (byte.TryParse(hex, NumberStyles.HexNumber, CultureInfo.InvariantCulture, out var b))
                        sb.Append((char)b);
                    i += 2;
                }
                continue;
            }

            if (!char.IsLetter(ch))
            {
                if (ch is '~' or '-' or '_') sb.Append(' ');
                continue;
            }

            int wordStart = i;
            while (i < value.Length && char.IsLetter(value[i])) i++;
            var controlWord = value[wordStart..i];

            if (i < value.Length && (value[i] == '-' || char.IsDigit(value[i])))
            {
                int numberStart = i;
                i++;
                while (i < value.Length && char.IsDigit(value[i])) i++;

                if (controlWord == "u" && int.TryParse(value[numberStart..i], out var codePoint))
                    sb.Append(char.ConvertFromUtf32(codePoint < 0 ? codePoint + 65536 : codePoint));
            }

            if (controlWord is "par" or "line" or "tab")
                sb.Append(' ');

            if (i < value.Length && value[i] != ' ')
                i--;
        }

        return sb.ToString();
    }
}
