using System.IO;
using System.Net;
using AngleSharp.Html.Parser;
using Ganss.Xss;

namespace FileSearch.Gui.Services;

public sealed class HtmlPreviewContent
{
    private readonly Dictionary<string, PreviewResource> _resources = new(StringComparer.Ordinal);
    private int _bytes;
    public IReadOnlyDictionary<string, PreviewResource> Resources => _resources;

    public string AddResource(PreviewResource resource)
    {
        _bytes += resource.Bytes.Length;
        if (_bytes > 32 * 1024 * 1024) throw new InvalidDataException("Document images exceed the 32 MB preview limit.");
        var url = "https://filesearch-preview.invalid/" + Guid.NewGuid().ToString("N");
        _resources.Add(url, resource);
        return url;
    }

    public string Sanitize(string html, Func<string, PreviewResource?>? imageLoader = null)
    {
        var document = new HtmlParser().ParseDocument(html);
        var imageUrls = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var element in document.QuerySelectorAll("script,style,iframe,object,embed,form,input,button,meta,link,base")) element.Remove();
        foreach (var image in document.QuerySelectorAll("img"))
        {
            string source = image.GetAttribute("src") ?? "";
            if (_resources.ContainsKey(source)) { image.RemoveAttribute("srcset"); continue; }
            if (imageUrls.TryGetValue(source, out var existing)) { image.SetAttribute("src", existing); image.RemoveAttribute("srcset"); continue; }
            var resource = imageLoader?.Invoke(source);
            if (resource is null) { image.RemoveAttribute("src"); image.SetAttribute("alt", image.GetAttribute("alt") ?? "Image unavailable"); }
            else { string url = AddResource(resource); image.SetAttribute("src", url); imageUrls[source] = url; }
            image.RemoveAttribute("srcset");
        }
        var sanitizer = new HtmlSanitizer { KeepChildNodes = true };
        sanitizer.AllowedTags.Clear();
        foreach (var tag in new[] { "p", "div", "span", "h1", "h2", "h3", "h4", "h5", "h6", "strong", "b", "em", "i", "u", "s", "sup", "sub", "br", "hr", "pre", "code", "ul", "ol", "li", "blockquote", "table", "thead", "tbody", "tr", "td", "th", "img" }) sanitizer.AllowedTags.Add(tag);
        sanitizer.AllowedAttributes.Clear();
        foreach (var attribute in new[] { "style", "colspan", "rowspan", "alt", "src" }) sanitizer.AllowedAttributes.Add(attribute);
        sanitizer.AllowedCssProperties.Clear();
        foreach (var property in new[] { "color", "background-color", "font-size", "font-weight", "font-style", "font-family", "text-align", "text-decoration", "white-space", "padding", "margin", "border", "border-collapse" }) sanitizer.AllowedCssProperties.Add(property);
        sanitizer.AllowedSchemes.Clear();
        sanitizer.AllowedSchemes.Add("https");
        return sanitizer.Sanitize(document.Body?.InnerHtml ?? "");
    }

    public DocumentPreview Document(string summary, string html, IReadOnlyList<PreviewPart>? parts = null, string? selected = null) =>
        new(DocumentPreviewKind.Html, summary, DocumentPreviewService.WrapHtml(html), Parts: parts, SelectedPart: selected, Resources: Resources);

    public static string Encode(string? value) => WebUtility.HtmlEncode(value ?? "");
}
