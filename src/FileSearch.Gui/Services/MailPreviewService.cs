using System.Globalization;
using System.IO;
using System.Text;
using FileSearch.Core.Extractors;
using MimeKit;

namespace FileSearch.Gui.Services;

public static class MailPreviewService
{
    public static Task<DocumentPreview> LoadAsync(string path, MailMessageMetadata? metadata, CancellationToken token) => Task.Run(async () =>
    {
        var content = new HtmlPreviewContent();
        string subject, from, to, cc, date, body;
        var attachments = new List<string>();
        if (Path.GetExtension(path).Equals(".eml", StringComparison.OrdinalIgnoreCase))
        {
            if (new FileInfo(path).Length > PreviewPackage.MaxEntryBytes) throw new InvalidDataException("Message files are limited to 64 MB.");
            await using var stream = File.OpenRead(path);
            using var message = await MimeMessage.LoadAsync(stream, cancellationToken: token).ConfigureAwait(false);
            subject = message.Subject ?? "(no subject)"; from = message.From.ToString(); to = message.To.ToString(); cc = message.Cc.ToString();
            date = message.Date == DateTimeOffset.MinValue ? "" : message.Date.ToString("g", CultureInfo.CurrentCulture);
            foreach (var attachment in message.Attachments.Take(1000)) attachments.Add(attachment is MimePart part ? part.FileName ?? part.ContentType.MimeType : "Attached message");
            string? html = message.HtmlBody;
            string text = message.TextBody ?? "";
            CheckBody(html ?? text);
            PreviewResource? InlineImage(string source)
            {
                if (!source.StartsWith("cid:", StringComparison.OrdinalIgnoreCase)) return null;
                string id = source[4..].Trim('<', '>');
                var part = message.BodyParts.OfType<MimePart>().FirstOrDefault(part => part.ContentId == id);
                if (part is null || part.Content is not { Stream: { } sourceStream } mimeContent || !part.ContentType.MimeType.StartsWith("image/", StringComparison.OrdinalIgnoreCase) || sourceStream.Length > 8 * 1024 * 1024) return null;
                using var output = new MemoryStream();
                mimeContent.DecodeTo(output, token);
                if (output.Length > 8 * 1024 * 1024) return null;
                return new PreviewResource(output.ToArray(), part.ContentType.MimeType);
            }
            body = html is null ? "<pre style='white-space:pre-wrap'>" + HtmlPreviewContent.Encode(text) + "</pre>" : content.Sanitize(html, InlineImage);
        }
        else
        {
            if (!OutlookMailReader.IsStore(path) && new FileInfo(path).Length > PreviewPackage.MaxEntryBytes) throw new InvalidDataException("Message files are limited to 64 MB.");
            var message = metadata is null ? OutlookMailReader.ReadMsg(path) : OutlookMailReader.ReadMessage(path, metadata, token);
            var mail = message.Metadata;
            subject = mail.Subject; from = mail.From; to = mail.To; cc = mail.Cc; date = mail.DateUtc?.ToLocalTime().ToString("g", CultureInfo.CurrentCulture) ?? "";
            CheckBody(message.HtmlBody ?? message.Body);
            body = message.HtmlBody is null ? "<pre style='white-space:pre-wrap'>" + HtmlPreviewContent.Encode(message.Body) + "</pre>" : content.Sanitize(message.HtmlBody);
            attachments.AddRange(message.AttachmentNames.Take(1000));
        }
        var header = new StringBuilder("<h2>" + HtmlPreviewContent.Encode(subject) + "</h2><table>");
        foreach (var (label, value) in new[] { ("From", from), ("To", to), ("Cc", cc), ("Date", date), ("Folder", metadata?.Folder ?? "") })
            if (!string.IsNullOrWhiteSpace(value)) header.Append("<tr><th>").Append(label).Append("</th><td>").Append(HtmlPreviewContent.Encode(value)).Append("</td></tr>");
        header.Append("</table><hr>").Append(body);
        if (attachments.Count > 0)
        {
            header.Append("<hr><h3>Attachments</h3><ul>");
            foreach (var attachment in attachments) header.Append("<li>").Append(HtmlPreviewContent.Encode(attachment)).Append("</li>");
            header.Append("</ul>");
        }
        return content.Document("Complete selected message · Remote content blocked", header.ToString());
    }, token);

    private static void CheckBody(string body)
    {
        if (body.Length > 4 * 1024 * 1024) throw new InvalidDataException("The message body exceeds the 4-million-character preview limit.");
    }
}
