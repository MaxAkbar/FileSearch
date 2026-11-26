using FileSearch.Gui.Services;

namespace FileSearch.Gui.Tests;

public sealed class MailPreviewMimeTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "FileSearchMailMime-" + Guid.NewGuid().ToString("N"));

    public MailPreviewMimeTests() => Directory.CreateDirectory(_root);

    public void Dispose() => Directory.Delete(_root, recursive: true);

    [Theory]
    [InlineData("multipart/mixed")]
    [InlineData("multipart/signed; protocol=\"application/pgp-signature\"; micalg=pgp-sha256")]
    public async Task EncodedMultipartMailPreviewsHtmlAndAttachmentsWithoutCryptographicVerification(string contentType)
    {
        // Use wire-format MIME rather than MimeKit's serializer to cover parser changes.
        // A signed envelope must still expose its readable body without a crypto context.
        string mime = $$"""
            From: =?UTF-8?Q?Jos=C3=A9?= <sender@example.invalid>
            To: Reader <reader@example.invalid>
            Subject: =?UTF-8?Q?R=C3=A9sum=C3=A9?=
            MIME-Version: 1.0
            Content-Type: {{contentType}}; boundary=outer

            --outer
            Content-Type: multipart/alternative; boundary=body

            --body
            Content-Type: text/plain; charset=windows-1252
            Content-Transfer-Encoding: quoted-printable

            Plain caf=E9 fallback
            --body
            Content-Type: text/html; charset=utf-8
            Content-Transfer-Encoding: quoted-printable

            <p><strong>caf=C3=A9</strong></p><script>unsafe()</script><img src=3D"https://example.invalid/tracker.png">
            --body--
            --outer
            Content-Type: application/pgp-signature
            Content-Disposition: attachment; filename*=utf-8''r%C3%A9sum%C3%A9.txt
            Content-Transfer-Encoding: base64

            c2lnbmF0dXJl
            --outer--
            """;
        string path = Path.Combine(_root, "multipart.eml");
        await File.WriteAllTextAsync(path, mime.ReplaceLineEndings("\r\n"), TestContext.Current.CancellationToken);

        var document = await MailPreviewService.LoadAsync(path, null, TestContext.Current.CancellationToken);

        Assert.Equal(DocumentPreviewKind.Html, document.Kind);
        Assert.Contains(HtmlPreviewContent.Encode("Résumé"), document.Text);
        Assert.Contains(HtmlPreviewContent.Encode("José"), document.Text);
        Assert.Contains("<strong>café</strong>", document.Text);
        Assert.Contains(HtmlPreviewContent.Encode("résumé.txt"), document.Text);
        Assert.DoesNotContain("Plain", document.Text);
        Assert.DoesNotContain("<script", document.Text);
        Assert.DoesNotContain("tracker.png", document.Text);
        // Preview content remains usable after the message and its source stream close.
        File.Move(path, path + ".moved");
    }

    [Fact]
    public async Task EncodedPlainTextMailDecodesLegacyCharsetAndEscapesMarkup()
    {
        string mime = "MIME-Version: 1.0\r\nContent-Type: text/plain; charset=windows-1252\r\nContent-Transfer-Encoding: quoted-printable\r\n\r\nPlain caf=E9 <script>unsafe()</script>";
        string path = Path.Combine(_root, "plain.eml");
        await File.WriteAllTextAsync(path, mime, TestContext.Current.CancellationToken);

        var document = await MailPreviewService.LoadAsync(path, null, TestContext.Current.CancellationToken);

        Assert.Equal(DocumentPreviewKind.Html, document.Kind);
        Assert.Contains(HtmlPreviewContent.Encode("Plain café <script>unsafe()</script>"), document.Text);
        Assert.DoesNotContain("<script", document.Text);
        File.Move(path, path + ".moved");
    }
}
