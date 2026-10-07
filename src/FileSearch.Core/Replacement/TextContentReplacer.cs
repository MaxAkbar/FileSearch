using System.Text;
using FileSearch.Core.Extractors;

namespace FileSearch.Core.Replacement;

internal static class TextContentReplacer
{
    public static bool Supports(string path, ReplacementRequest request) =>
        TextFileExtensions.All.Contains(Path.GetExtension(path)) ||
        request.AdditionalTextExtensions?.Contains(Path.GetExtension(path)) == true ||
        string.IsNullOrEmpty(Path.GetExtension(path)) || Path.GetFileName(path).StartsWith('.') && Path.GetFileName(path).LastIndexOf('.') == 0;

    public static ContentReplacement Replace(byte[] bytes, ReplacementMatcher matcher)
    {
        Encoding encoding = new UTF8Encoding(false, true);
        var preambleLength = 0;
        if (bytes.AsSpan().StartsWith(new byte[] { 0xff, 0xfe, 0, 0 })) { encoding = new UTF32Encoding(false, true, true); preambleLength = 4; }
        else if (bytes.AsSpan().StartsWith(new byte[] { 0, 0, 0xfe, 0xff })) { encoding = new UTF32Encoding(true, true, true); preambleLength = 4; }
        else if (bytes.AsSpan().StartsWith(new byte[] { 0xff, 0xfe })) { encoding = new UnicodeEncoding(false, true, true); preambleLength = 2; }
        else if (bytes.AsSpan().StartsWith(new byte[] { 0xfe, 0xff })) { encoding = new UnicodeEncoding(true, true, true); preambleLength = 2; }
        else if (bytes.AsSpan().StartsWith(new byte[] { 0xef, 0xbb, 0xbf })) preambleLength = 3;
        var text = encoding.GetString(bytes, preambleLength, bytes.Length - preambleLength);
        if (text.Contains('\0')) throw new NotSupportedException("Binary file or unsupported text encoding.");
        var edits = matcher.FindChanges(text);
        var count = edits.Count;
        var replaced = ReplacementMatcher.ApplyChanges(text, edits);
        var body = encoding.GetBytes(replaced);
        var output = new byte[preambleLength + body.Length];
        bytes.AsSpan(0, preambleLength).CopyTo(output);
        body.CopyTo(output, preambleLength);
        // The preview is bounded; counts and the actual edit always cover the whole file.
        var changes = new List<ReplacementChange>();
        var index = 0;
        while (index < edits.Count && changes.Count < 100)
        {
            var first = index;
            var edit = edits[index];
            var start = Math.Max(0, edit.Start - 80);
            var end = Math.Min(text.Length, edit.Start + edit.Length + 80);
            // Merge overlapping excerpts so After represents every edit in that excerpt.
            while (index + 1 < edits.Count && edits[index + 1].Start - 80 <= end)
            {
                index++;
                end = Math.Min(text.Length, edits[index].Start + edits[index].Length + 80);
            }
            var before = text[start..end];
            var local = edits.Skip(first).Take(index - first + 1).Select(change => change with { Start = change.Start - start }).ToArray();
            var after = ReplacementMatcher.ApplyChanges(before, local);
            changes.Add(new($"Character {edit.Start + 1:N0}", Preview(before), Preview(after)));
            index++;
        }
        if (index < edits.Count) changes.Add(new("Preview limit", "", "Additional changes are counted and will be applied."));
        return new(output, count, changes);
    }

    internal static string Preview(string text) => text.Length > 16000 ? text[..16000] + "\n… (preview truncated; all changes counted)" : text;
}
