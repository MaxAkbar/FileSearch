using System.Net;
using System.Text;
using System.Text.RegularExpressions;

namespace FileSearch.Core.Extractors;

internal static class MarkupText
{
    private static readonly Regex s_blockTagRegex = new(@"<\s*(br|p|div|li|tr|td|th|h[1-6]|section|article|header|footer)\b[^>]*>", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
    private static readonly Regex s_tagRegex = new("<[^>]+>", RegexOptions.CultureInvariant);
    private static readonly Regex s_scriptAndStyleRegex = new(@"<\s*(script|style)\b[^>]*>.*?<\s*/\s*\1\s*>", RegexOptions.IgnoreCase | RegexOptions.Singleline | RegexOptions.CultureInvariant);
    private static readonly Regex s_commentRegex = new(@"<!--.*?-->", RegexOptions.Singleline | RegexOptions.CultureInvariant);

    public static string FromXml(string value)
    {
        if (string.IsNullOrWhiteSpace(value)) return string.Empty;

        var withBreaks = s_blockTagRegex.Replace(value, " ");
        var withoutTags = s_tagRegex.Replace(withBreaks, " ");
        return Normalize(WebUtility.HtmlDecode(withoutTags));
    }

    public static string FromHtml(string value)
    {
        if (string.IsNullOrWhiteSpace(value)) return string.Empty;

        var withoutScripts = s_scriptAndStyleRegex.Replace(value, " ");
        var withoutComments = s_commentRegex.Replace(withoutScripts, " ");
        return FromXml(withoutComments);
    }

    public static string Normalize(string value)
    {
        if (string.IsNullOrWhiteSpace(value)) return string.Empty;

        var sb = new StringBuilder(value.Length);
        bool inWhitespace = false;

        foreach (var ch in value)
        {
            if (char.IsWhiteSpace(ch))
            {
                if (!inWhitespace)
                {
                    sb.Append(' ');
                    inWhitespace = true;
                }
            }
            else
            {
                sb.Append(ch);
                inWhitespace = false;
            }
        }

        return sb.ToString().Trim();
    }

}
