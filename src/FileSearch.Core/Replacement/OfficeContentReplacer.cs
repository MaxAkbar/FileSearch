using System.IO.Compression;
using System.Xml.Linq;
using DocumentFormat.OpenXml.Packaging;
using DocumentFormat.OpenXml.Validation;

namespace FileSearch.Core.Replacement;

/// <summary>Edits native XML text in a copied package; unrelated package parts stay intact.</summary>
internal static class OfficeContentReplacer
{
    private static readonly XNamespace Word = "http://schemas.openxmlformats.org/wordprocessingml/2006/main";
    private static readonly XNamespace Drawing = "http://schemas.openxmlformats.org/drawingml/2006/main";
    private static readonly XNamespace Sheet = "http://schemas.openxmlformats.org/spreadsheetml/2006/main";
    private static readonly XNamespace Relationships = "http://schemas.openxmlformats.org/officeDocument/2006/relationships";

    public static bool Supports(string path) => Path.GetExtension(path).ToLowerInvariant() is ".docx" or ".xlsx" or ".pptx";

    public static ContentReplacement Replace(string path, byte[] bytes, ReplacementMatcher matcher, bool includeFormulas)
    {
        var extension = Path.GetExtension(path).ToLowerInvariant();
        using var memory = new MemoryStream();
        memory.Write(bytes);
        var count = 0;
        var changes = new List<ReplacementChange>();
        using (var archive = new ZipArchive(memory, ZipArchiveMode.Update, leaveOpen: true))
        {
            if (archive.Entries.Any(entry => entry.FullName.StartsWith("_xmlsignatures/", StringComparison.OrdinalIgnoreCase)))
                throw new NotSupportedException("Signed Office documents cannot be replaced.");
            var xmlParts = archive.Entries.Where(entry => entry.FullName.EndsWith(".xml", StringComparison.OrdinalIgnoreCase)).ToArray();
            foreach (var entry in xmlParts)
            {
                // Protection is a document constraint, rather than permission to edit its XML directly.
                if (entry.FullName.StartsWith("word/", StringComparison.Ordinal) || entry.FullName.StartsWith("xl/", StringComparison.Ordinal) || entry.FullName.StartsWith("ppt/", StringComparison.Ordinal))
                {
                    var xml = Read(entry);
                    if (xml.Descendants().Any(element => element.Name.LocalName is "documentProtection" or "sheetProtection" or "workbookProtection" or "modifyVerifier"))
                        throw new NotSupportedException("Protected Office documents cannot be replaced.");
                }
            }

            if (extension == ".xlsx")
                count = ReplaceWorkbook(archive, matcher, includeFormulas, changes);
            else
            {
                foreach (var entry in xmlParts)
                {
                    var isWordPart = extension == ".docx" && (entry.FullName == "word/document.xml" ||
                        entry.FullName.StartsWith("word/header", StringComparison.Ordinal) || entry.FullName.StartsWith("word/footer", StringComparison.Ordinal));
                    var isSlidePart = extension == ".pptx" && (entry.FullName.StartsWith("ppt/slides/slide", StringComparison.Ordinal) ||
                        entry.FullName.StartsWith("ppt/notesSlides/notesSlide", StringComparison.Ordinal));
                    if (!isWordPart && !isSlidePart) continue;
                    var ns = isWordPart ? Word : Drawing;
                    var xml = Read(entry);
                    var partCount = 0;
                    var paragraphNumber = 0;
                    foreach (var paragraph in xml.Descendants(ns + "p"))
                    {
                        paragraphNumber++;
                        if (isWordPart)
                        {
                            // Fields and nested textbox paragraphs delimit text, rather than flattening into an editable match.
                            var texts = new List<XElement>();
                            var fieldDepth = 0;
                            foreach (var node in paragraph.Descendants().Where(node => node.Ancestors(Word + "p").FirstOrDefault() == paragraph))
                            {
                                if (node.Name == Word + "fldChar")
                                {
                                    partCount += ReplaceRuns(texts.ToArray(), matcher, $"{entry.FullName}, paragraph {paragraphNumber}", changes); texts.Clear();
                                    if (node.Attribute(Word + "fldCharType")?.Value == "begin") fieldDepth++;
                                    if (node.Attribute(Word + "fldCharType")?.Value == "end") fieldDepth = Math.Max(0, fieldDepth - 1);
                                }
                                else if (node.Name.Namespace == Word && node.Name.LocalName is "tab" or "br" or "cr" or "drawing" or "pict")
                                { partCount += ReplaceRuns(texts.ToArray(), matcher, $"{entry.FullName}, paragraph {paragraphNumber}", changes); texts.Clear(); }
                                else if (node.Name == Word + "t")
                                {
                                    if (fieldDepth > 0 || node.Ancestors().Any(parent => parent.Name == Word + "fldSimple" || parent.Name == Word + "del"))
                                    { partCount += ReplaceRuns(texts.ToArray(), matcher, $"{entry.FullName}, paragraph {paragraphNumber}", changes); texts.Clear(); }
                                    else texts.Add(node);
                                }
                            }
                            partCount += ReplaceRuns(texts.ToArray(), matcher, $"{entry.FullName}, paragraph {paragraphNumber}", changes);
                        }
                        else
                        {
                            var texts = new List<XElement>();
                            foreach (var node in paragraph.Elements())
                            {
                                if (node.Name == Drawing + "r") texts.AddRange(node.Elements(Drawing + "t"));
                                else { partCount += ReplaceRuns(texts.ToArray(), matcher, $"{entry.FullName}, paragraph {paragraphNumber}", changes); texts.Clear(); }
                            }
                            partCount += ReplaceRuns(texts.ToArray(), matcher, $"{entry.FullName}, paragraph {paragraphNumber}", changes);
                        }
                    }
                    if (partCount > 0) Write(entry, xml);
                    count += partCount;
                }
            }
        }
        var output = count == 0 ? bytes : memory.ToArray();
        if (count > 0) ValidatePackage(path, bytes, output);
        return new(output, count, changes);
    }

    private static int ReplaceWorkbook(ZipArchive archive, ReplacementMatcher matcher, bool includeFormulas, List<ReplacementChange> changes)
    {
        var stringsEntry = archive.GetEntry("xl/sharedStrings.xml");
        var strings = stringsEntry is null ? Array.Empty<XElement>() : Read(stringsEntry).Root!.Elements(Sheet + "si").ToArray();
        var workbookEntry = archive.GetEntry("xl/workbook.xml") ?? throw new InvalidDataException("Workbook part is missing.");
        var workbook = Read(workbookEntry);
        var relationEntry = archive.GetEntry("xl/_rels/workbook.xml.rels");
        var relations = relationEntry is null ? new Dictionary<string, string>() : Read(relationEntry).Root!.Elements()
            .Where(element => element.Attribute("TargetMode")?.Value != "External")
            .ToDictionary(element => element.Attribute("Id")!.Value, element => ResolvePart("xl/workbook.xml", element.Attribute("Target")!.Value));
        var sheetNames = workbook.Descendants(Sheet + "sheet").Where(element => element.Attribute(Relationships + "id") is not null)
            .Where(element => relations.ContainsKey(element.Attribute(Relationships + "id")!.Value))
            .ToDictionary(element => relations[element.Attribute(Relationships + "id")!.Value], element => element.Attribute("name")?.Value ?? "Sheet");
        var count = 0;
        var formulasChanged = false;
        foreach (var entry in archive.Entries.Where(entry => entry.FullName.StartsWith("xl/worksheets/", StringComparison.Ordinal) && entry.FullName.EndsWith(".xml", StringComparison.Ordinal)).ToArray())
        {
            var xml = Read(entry);
            var partCount = 0;
            foreach (var cell in xml.Descendants(Sheet + "c"))
            {
                var location = $"{sheetNames.GetValueOrDefault(entry.FullName, entry.FullName)}!{cell.Attribute("r")?.Value}";
                var formula = cell.Element(Sheet + "f");
                if (formula is not null)
                {
                    if (!includeFormulas) continue;
                    var after = matcher.Replace(formula.Value, out var formulaCount);
                    if (formulaCount == 0) continue;
                    if (formula.Attribute("t")?.Value is "shared" or "array" or "dataTable" || cell.Attribute("cm") is not null)
                    {
                        AddPreview(changes, new(location + " (formula skipped)", formula.Value, "Shared, array, and spill formulas are not supported."));
                        continue;
                    }
                    AddPreview(changes, new(location + " (formula)", formula.Value, after));
                    formula.Value = after;
                    cell.Element(Sheet + "v")?.Remove();
                    partCount += formulaCount;
                    formulasChanged = true;
                    continue;
                }
                var type = cell.Attribute("t")?.Value;
                XElement? inline;
                if (type == "s")
                {
                    if (!int.TryParse(cell.Element(Sheet + "v")?.Value, out var index) || index < 0 || index >= strings.Length)
                        throw new InvalidDataException("Invalid shared-string index.");
                    inline = new XElement(Sheet + "is", strings[index].Nodes().Select(CloneNode));
                }
                else if (type == "inlineStr") inline = cell.Element(Sheet + "is");
                else if (type == "str") inline = new XElement(Sheet + "is", new XElement(Sheet + "t", cell.Element(Sheet + "v")?.Value ?? ""));
                else continue; // Numbers, dates, Booleans and errors remain their original types.
                if (inline is null) continue;
                var changed = ReplaceRuns(inline.Descendants(Sheet + "t").ToArray(), matcher, location, changes);
                if (changed == 0) continue;
                if (type != "inlineStr")
                {
                    cell.Element(Sheet + "v")?.Remove();
                    cell.SetAttributeValue("t", "inlineStr");
                    if (cell.Element(Sheet + "extLst") is { } extensions) extensions.AddBeforeSelf(inline);
                    else cell.Add(inline);
                }
                partCount += changed;
            }
            if (partCount > 0) Write(entry, xml);
            count += partCount;
        }
        if (formulasChanged)
        {
            var calculation = workbook.Root!.Element(Sheet + "calcPr");
            if (calculation is null)
            {
                calculation = new XElement(Sheet + "calcPr");
                var following = workbook.Root.Elements().FirstOrDefault(element => element.Name.LocalName is "oleSize" or "customWorkbookViews" or "pivotCaches" or "smartTagPr" or "smartTagTypes" or "webPublishing" or "fileRecoveryPr" or "webPublishObjects" or "extLst");
                if (following is not null) following.AddBeforeSelf(calculation); else workbook.Root.Add(calculation);
            }
            calculation.SetAttributeValue("fullCalcOnLoad", "1");
            calculation.SetAttributeValue("forceFullCalc", "1");
            calculation.SetAttributeValue("calcMode", "auto");
            Write(workbookEntry, workbook);
        }
        return count;
    }

    private static int ReplaceRuns(XElement[] texts, ReplacementMatcher matcher, string location, List<ReplacementChange> previews)
    {
        if (texts.Length == 0) return 0;
        var before = string.Concat(texts.Select(text => text.Value));
        var edits = matcher.FindChanges(before);
        if (edits.Count == 0) return 0;
        var offsets = new int[texts.Length];
        for (var i = 1; i < texts.Length; i++) offsets[i] = offsets[i - 1] + texts[i - 1].Value.Length;
        // Work backwards so offsets before each edit remain valid. Insert in the run owning the first character.
        foreach (var edit in edits.Reverse())
        {
            var startRun = Array.FindLastIndex(offsets, offset => offset <= edit.Start);
            if (startRun < 0) continue;
            var end = edit.Start + edit.Length;
            for (var i = texts.Length - 1; i >= startRun; i--)
            {
                var from = Math.Clamp(edit.Start - offsets[i], 0, texts[i].Value.Length);
                var to = Math.Clamp(end - offsets[i], 0, texts[i].Value.Length);
                if (to > from)
                {
                    texts[i].Value = texts[i].Value.Remove(from, to - from);
                    texts[i].SetAttributeValue(XNamespace.Xml + "space", "preserve");
                }
            }
            texts[startRun].Value = texts[startRun].Value.Insert(edit.Start - offsets[startRun], edit.Text);
            texts[startRun].SetAttributeValue(XNamespace.Xml + "space", "preserve");
        }
        AddPreview(previews, new(location, TextContentReplacer.Preview(before), TextContentReplacer.Preview(string.Concat(texts.Select(text => text.Value)))));
        return edits.Count;
    }

    private static void AddPreview(List<ReplacementChange> changes, ReplacementChange change)
    {
        if (changes.Count < 100) changes.Add(change);
        else if (changes.Count == 100) changes.Add(new("Preview limit", "", "Additional changes are counted and will be applied."));
    }

    private static XNode CloneNode(XNode node) => node is XElement element ? new XElement(element) : new XText(node.ToString());
    private static string ResolvePart(string owner, string target) => new Uri(new Uri("http://package/" + owner), target).AbsolutePath.TrimStart('/');
    private static XDocument Read(ZipArchiveEntry entry) { using var stream = entry.Open(); return XDocument.Load(stream, LoadOptions.PreserveWhitespace); }
    private static void Write(ZipArchiveEntry entry, XDocument xml) { using var stream = entry.Open(); stream.SetLength(0); xml.Save(stream, SaveOptions.DisableFormatting); }

    private static void ValidatePackage(string path, byte[] original, byte[] updated)
    {
        using var before = Open(path, new MemoryStream(original));
        using var after = Open(path, new MemoryStream(updated));
        var validator = new OpenXmlValidator();
        var previousErrors = validator.Validate(before).Select(ErrorKey).ToHashSet(StringComparer.Ordinal);
        var introduced = validator.Validate(after).FirstOrDefault(error => !previousErrors.Contains(ErrorKey(error)));
        if (introduced is not null) throw new InvalidDataException("Replacement would invalidate the Office document: " + introduced.Description);
    }

    private static string ErrorKey(ValidationErrorInfo error) => $"{error.Id}|{error.Part?.Uri}|{error.Path?.XPath}|{error.Description}";
    private static OpenXmlPackage Open(string path, Stream stream) => Path.GetExtension(path).ToLowerInvariant() switch
    {
        ".docx" => WordprocessingDocument.Open(stream, false),
        ".xlsx" => SpreadsheetDocument.Open(stream, false),
        ".pptx" => PresentationDocument.Open(stream, false),
        _ => throw new NotSupportedException(),
    };
}
