using System.Runtime.CompilerServices;

namespace FileSearch.Core.Extractors;

public sealed class MsgExtractor : IDiagnosticTextExtractor
{
    private readonly OutlookMailOptions _options;
    public MsgExtractor(OutlookMailOptions? options = null) => _options = options ?? new OutlookMailOptions();
    public string ExtractorId => "filesearch.outlook-msg";
    public string ExtractorVersion => "1";
    public IReadOnlyCollection<string> SupportedExtensions { get; } = new[] { ".msg" };
    public IAsyncEnumerable<TextLine> ExtractAsync(string path, CancellationToken cancellationToken) =>
        ExtractAsync(path, NullExtractionIssueSink.Instance, cancellationToken);

    public async IAsyncEnumerable<TextLine> ExtractAsync(string path, IExtractionIssueSink issues,
        [EnumeratorCancellation] CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var message = OutlookMailReader.ReadMsg(path);
        var number = 0;
        foreach (var line in OutlookMailExtractor.Format(message, _options.MaxMessageChars, issues))
        {
            cancellationToken.ThrowIfCancellationRequested();
            yield return line with { Number = ++number };
        }
        await Task.CompletedTask.ConfigureAwait(false);
    }
}

public sealed class OutlookStoreExtractor : IDiagnosticTextExtractor
{
    private readonly OutlookMailOptions _options;
    public OutlookStoreExtractor(OutlookMailOptions? options = null) => _options = options ?? new OutlookMailOptions();
    public string ExtractorId => "filesearch.outlook-store";
    public string ExtractorVersion => "1";
    public IReadOnlyCollection<string> SupportedExtensions { get; } = new[] { ".pst", ".ost" };
    public IAsyncEnumerable<TextLine> ExtractAsync(string path, CancellationToken cancellationToken) =>
        ExtractAsync(path, NullExtractionIssueSink.Instance, cancellationToken);

    public async IAsyncEnumerable<TextLine> ExtractAsync(string path, IExtractionIssueSink issues,
        [EnumeratorCancellation] CancellationToken cancellationToken)
    {
        var number = 0;
        long characters = 0;
        foreach (var message in OutlookMailReader.ReadStore(path, issues, cancellationToken, maxMessages: _options.MaxMessages))
        {
            cancellationToken.ThrowIfCancellationRequested();
            var numberedMessage = message with { Metadata = message.Metadata with { FirstLineNumber = number + 1 } };
            foreach (var line in OutlookMailExtractor.Format(numberedMessage, _options.MaxMessageChars, issues))
            {
                cancellationToken.ThrowIfCancellationRequested();
                // Anchors accompany every persisted line and host response. Include
                // repeated headers in the budget so a store of tiny bodies with
                // large recipient lists cannot produce an unbounded index payload.
                var metadata = numberedMessage.Metadata;
                characters += line.Content.Length + 200L + metadata.Subject.Length + metadata.From.Length +
                    metadata.To.Length + metadata.Cc.Length + metadata.Folder.Length;
                if (characters > _options.MaxStoreChars)
                {
                    issues.Report(new ExtractionIssue(null, "mail_store_limit", "The mail store text limit was reached; later messages were not searched."));
                    yield break;
                }
                yield return line with { Number = ++number };
            }
        }
        await Task.CompletedTask.ConfigureAwait(false);
    }
}

internal static class OutlookMailExtractor
{
    public static IEnumerable<TextLine> Format(OutlookMailMessage message, int maxChars, IExtractionIssueSink issues)
    {
        var metadata = message.Metadata;
        var anchor = new SourceAnchor(SourceAnchorKind.Email,
            string.IsNullOrWhiteSpace(metadata.Folder) ? metadata.Subject : $"{metadata.Folder} · {metadata.Subject}",
            Section: metadata.Folder, MemberPath: metadata.StoreFingerprint is null ? null : metadata.Id,
            MailMessage: metadata);
        var characters = 0;
        foreach (var content in OutlookMailText.Lines(message))
        {
            characters += content.Length;
            if (characters > maxChars)
            {
                issues.Report(new ExtractionIssue(metadata.Id, "mail_message_limit", "The message text limit was reached; the remaining content was not searched."));
                yield break;
            }
            yield return new TextLine(0, content, anchor);
        }
    }
}
