using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net;
using System.Text;
using System.Text.RegularExpressions;
using Microsoft.Build.Framework;

namespace FileSearch.Build
{
    public sealed class GenerateHelpSearchIndex : Microsoft.Build.Utilities.Task
    {
        [Required] public ITaskItem[] HelpFiles { get; set; }
        [Required] public string OutputFile { get; set; }

        public override bool Execute()
        {
            try
            {
                var documents = new List<Dictionary<string, string>>();
                foreach (var file in HelpFiles.OrderBy(item => item.ItemSpec, StringComparer.OrdinalIgnoreCase))
                {
                    var html = File.ReadAllText(file.ItemSpec);
                    var main = Regex.Match(html, @"<main\b[^>]*>(.*?)</main\s*>", RegexOptions.Singleline | RegexOptions.IgnoreCase);
                    if (!main.Success)
                        throw new InvalidDataException("Help page has no main content: " + file.ItemSpec);

                    var page = Path.GetFileName(file.ItemSpec);
                    var title = PlainText(Regex.Match(html, @"<title\b[^>]*>(.*?)</title\s*>", RegexOptions.Singleline | RegexOptions.IgnoreCase).Groups[1].Value);
                    title = title.Replace(" - FileSearch Help", "");
                    if (title == "FileSearch Help") title = "Overview";
                    var content = Regex.Replace(main.Groups[1].Value,
                        "<div\\s+class=\"next-links\"[^>]*>.*?</div\\s*>", "", RegexOptions.Singleline | RegexOptions.IgnoreCase);
                    var headings = Regex.Matches(content,
                        @"<h(?<level>[23])\b(?<attributes>[^>]*)>(?<heading>.*?)</h\k<level>\s*>",
                        RegexOptions.Singleline | RegexOptions.IgnoreCase);

                    Add(documents, page, title, "Overview", "", content.Substring(0, headings.Count == 0 ? content.Length : headings[0].Index));
                    for (var i = 0; i < headings.Count; i++)
                    {
                        var heading = headings[i];
                        var id = Regex.Match(heading.Groups["attributes"].Value, "(?:^|\\s)id\\s*=\\s*[\"']([^\"']+)[\"']", RegexOptions.IgnoreCase);
                        var anchor = id.Success ? WebUtility.HtmlDecode(id.Groups[1].Value) : "help-section-" + i;
                        var start = heading.Index + heading.Length;
                        var end = i + 1 < headings.Count ? headings[i + 1].Index : content.Length;
                        Add(documents, page, title, PlainText(heading.Groups["heading"].Value), anchor, content.Substring(start, end - start));
                    }
                }

                if (documents.Count == 0)
                    throw new InvalidDataException("No Help content was available to index.");

                var json = "[" + string.Join(",", documents.Select(document =>
                    "{" + string.Join(",", document.Select(field => Quote(field.Key) + ":" + Quote(field.Value))) + "}")) + "]";
                var script = "// Generated from the bundled Help pages.\nwindow.FileSearchHelpIndex = " + json + ";\n";
                Directory.CreateDirectory(Path.GetDirectoryName(OutputFile));
                if (!File.Exists(OutputFile) || File.ReadAllText(OutputFile) != script)
                    File.WriteAllText(OutputFile, script, new UTF8Encoding(false));
                return true;
            }
            catch (Exception error)
            {
                Log.LogErrorFromException(error);
                return false;
            }
        }

        private static void Add(List<Dictionary<string, string>> documents, string page, string title, string heading, string anchor, string html)
        {
            var text = PlainText(html);
            if (text.Length == 0) return;
            documents.Add(new Dictionary<string, string>
            {
                ["page"] = page, ["title"] = title, ["heading"] = heading, ["anchor"] = anchor, ["text"] = text,
            });
        }

        private static string PlainText(string html)
        {
            var text = Regex.Replace(html, @"<(script|style)\b[^>]*>.*?</\1\s*>", " ", RegexOptions.Singleline | RegexOptions.IgnoreCase);
            text = Regex.Replace(text, @"<[^>]+>", " ");
            return Regex.Replace(WebUtility.HtmlDecode(text), @"\s+", " ").Trim();
        }

        private static string Quote(string value)
        {
            var result = new StringBuilder("\"");
            foreach (var character in value)
            {
                if (character == '\\' || character == '"')
                    result.Append('\\').Append(character);
                else if (character < ' ' || character == '<' || character == '>' || character == '&' || character == '\u2028' || character == '\u2029')
                    result.Append("\\u").Append(((int)character).ToString("x4"));
                else
                    result.Append(character);
            }
            return result.Append('"').ToString();
        }
    }
}
