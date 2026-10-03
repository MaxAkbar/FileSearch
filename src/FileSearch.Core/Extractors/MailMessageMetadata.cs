namespace FileSearch.Core.Extractors;

/// <summary>Identifies one message in an unchanged local Outlook store.</summary>
public sealed record MailMessageMetadata(
    string Id,
    string Subject,
    string From,
    string To,
    string Cc,
    DateTime? DateUtc,
    string Folder,
    string? StoreFingerprint = null,
    int FirstLineNumber = 1);

public sealed record OutlookMailMessage(
    MailMessageMetadata Metadata,
    string Body,
    IReadOnlyList<string> AttachmentNames);

public sealed class OutlookMailOptions
{
    public int MaxMessageChars { get; set; } = 2 * 1024 * 1024;
    public int MaxStoreChars { get; set; } = 64 * 1024 * 1024;
    public int MaxMessages { get; set; } = 100_000;
}
