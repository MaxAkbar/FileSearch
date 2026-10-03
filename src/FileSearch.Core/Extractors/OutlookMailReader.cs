using System.Globalization;
using System.Runtime.CompilerServices;
using System.Text;
using XstReader;
using XstReader.ElementProperties;

namespace FileSearch.Core.Extractors;

/// <summary>Reads local mail files without COM, Outlook, or an installed IFilter.</summary>
public static class OutlookMailReader
{
    static OutlookMailReader() => Encoding.RegisterProvider(CodePagesEncodingProvider.Instance);

    public static bool IsStore(string path) =>
        Path.GetExtension(path).Equals(".pst", StringComparison.OrdinalIgnoreCase) ||
        Path.GetExtension(path).Equals(".ost", StringComparison.OrdinalIgnoreCase);

    public static string GetFingerprint(string path)
    {
        var info = new FileInfo(path);
        return string.Create(CultureInfo.InvariantCulture, $"{info.Length}:{info.LastWriteTimeUtc.Ticks}");
    }

    public static OutlookMailMessage ReadMsg(string path)
    {
        return MsgFileReader.Read(path);
    }

    public static IEnumerable<OutlookMailMessage> ReadStore(
        string path,
        IExtractionIssueSink issues,
        CancellationToken cancellationToken,
        string? messageId = null,
        int maxMessages = 100_000)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var fingerprint = GetFingerprint(path);
        using var store = new XstFile(path);
        var budget = new MailReadBudget(maxMessages);
        try
        {
            foreach (var message in ReadFolder(store.RootFolder, "0", string.Empty, fingerprint, issues, messageId, budget, 0, cancellationToken))
                yield return message;
            if (!string.Equals(fingerprint, GetFingerprint(path), StringComparison.Ordinal))
                throw new IOException("The Outlook store changed during extraction. Close Outlook and retry, or search a stable copy.");
        }
        finally
        {
            // XstReader.Api 1.0.6 closes its stream, then asynchronously clears the
            // root by re-enumerating lazy folders/messages. That reopens the stream
            // after Dispose and leaks a file handle. Detach the disposable cache:
            // our traversal retains no messages and normal GC releases the graph.
            // Keep this adapter tied to the pinned package and its handle-release tests.
            CachedRoot(store) = null;
        }
    }

    [UnsafeAccessor(UnsafeAccessorKind.Field, Name = "_RootFolder")]
    private static extern ref XstFolder? CachedRoot(XstFile store);

    public static OutlookMailMessage ReadMessage(string path, MailMessageMetadata metadata, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (!IsStore(path))
            return ReadMsg(path);
        if (!string.Equals(metadata.StoreFingerprint, GetFingerprint(path), StringComparison.Ordinal))
            throw new IOException("The Outlook store has changed. Refresh the index or search again before opening this message.");
        return ReadStore(path, NullExtractionIssueSink.Instance, cancellationToken, metadata.Id).SingleOrDefault()
            ?? throw new IOException("The message could not be read. Refresh the index or search again.");
    }

    public static IEnumerable<TextLine> ReadMessageLines(string path, MailMessageMetadata metadata, CancellationToken cancellationToken)
    {
        var message = ReadMessage(path, metadata, cancellationToken) with { Metadata = metadata };
        var number = metadata.FirstLineNumber;
        foreach (var line in OutlookMailExtractor.Format(message, 2 * 1024 * 1024, NullExtractionIssueSink.Instance))
        {
            cancellationToken.ThrowIfCancellationRequested();
            yield return line with { Number = number++ };
        }
    }

    /// <summary>Creates a text-only EML copy for the default mail client. Store attachments are never exported.</summary>
    public static async Task WriteMessagePreviewAsync(string path, MailMessageMetadata metadata, string outputPath,
        CancellationToken cancellationToken)
    {
        var message = await Task.Run(() => ReadMessage(path, metadata, cancellationToken), cancellationToken).ConfigureAwait(false);
        var text = string.Join('\n', OutlookMailExtractor.Format(message, 2 * 1024 * 1024, NullExtractionIssueSink.Instance)
            .Select(line => line.Content));
        await using var stream = new FileStream(outputPath, FileMode.CreateNew, FileAccess.Write, FileShare.None);
        await using var writer = new StreamWriter(stream, new UTF8Encoding(false));
        await writer.WriteLineAsync($"Subject: =?utf-8?B?{Convert.ToBase64String(Encoding.UTF8.GetBytes(metadata.Subject))}?=".AsMemory(), cancellationToken).ConfigureAwait(false);
        await writer.WriteLineAsync($"From: {Clean(metadata.From)}".AsMemory(), cancellationToken).ConfigureAwait(false);
        await writer.WriteLineAsync($"To: {Clean(metadata.To)}".AsMemory(), cancellationToken).ConfigureAwait(false);
        if (!string.IsNullOrWhiteSpace(metadata.Cc))
            await writer.WriteLineAsync($"Cc: {Clean(metadata.Cc)}".AsMemory(), cancellationToken).ConfigureAwait(false);
        if (metadata.DateUtc is { } date)
            await writer.WriteLineAsync(("Date: " + date.ToString("r", CultureInfo.InvariantCulture)).AsMemory(), cancellationToken).ConfigureAwait(false);
        await writer.WriteLineAsync("MIME-Version: 1.0\r\nContent-Type: text/plain; charset=utf-8\r\nContent-Transfer-Encoding: base64\r\n".AsMemory(), cancellationToken).ConfigureAwait(false);
        await writer.WriteLineAsync(Convert.ToBase64String(Encoding.UTF8.GetBytes(text), Base64FormattingOptions.InsertLineBreaks).AsMemory(), cancellationToken).ConfigureAwait(false);
    }

    private static IEnumerable<OutlookMailMessage> ReadFolder(
        XstFolder folder, string folderId, string folderPath, string fingerprint,
        IExtractionIssueSink issues, string? requestedId, MailReadBudget budget, int depth, CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        if (depth > 64)
        {
            issues.Report(new ExtractionIssue(folderPath, "mail_folder_depth", "Mail folder nesting exceeds 64 levels; this subtree was skipped."));
            yield break;
        }

        using (var messages = folder.Messages.GetEnumerator())
        {
            var ordinal = 0;
            while (TryMoveNext(messages, folderPath, issues, token))
            {
                // Count attempts, including corrupt messages, before loading bodies.
                if (requestedId is null && budget.Remaining-- <= 0)
                {
                    budget.Exhausted = true;
                    issues.Report(new ExtractionIssue(null, "mail_store_limit", "The mail store message limit was reached; later messages were not searched."));
                    yield break;
                }
                var id = string.Create(CultureInfo.InvariantCulture, $"{folderId}:{ordinal++}");
                if (requestedId is not null && !string.Equals(requestedId, id, StringComparison.Ordinal))
                    continue;
                OutlookMailMessage? extracted = null;
                try
                {
                    extracted = ReadStoreMessage(messages.Current, id, folderPath, fingerprint);
                }
                catch (Exception ex) when (ex is not OperationCanceledException)
                {
                    issues.Report(new ExtractionIssue(id, "mail_message_failed", ex.Message));
                }
                if (extracted is not null)
                    yield return extracted;
                if (requestedId is not null)
                    yield break;
            }
        }

        using var folders = folder.Folders.GetEnumerator();
        var childOrdinal = 0;
        while (TryMoveNext(folders, folderPath, issues, token))
        {
            var child = folders.Current;
            var childId = string.Create(CultureInfo.InvariantCulture, $"{folderId}.{childOrdinal++}");
            if (requestedId is not null && !requestedId.StartsWith(childId + ".", StringComparison.Ordinal) &&
                !requestedId.StartsWith(childId + ":", StringComparison.Ordinal))
                continue;
            var childPath = string.IsNullOrWhiteSpace(folderPath) ? Clean(child.DisplayName) : folderPath + "/" + Clean(child.DisplayName);
            foreach (var message in ReadFolder(child, childId, childPath, fingerprint, issues, requestedId, budget, depth + 1, token))
                yield return message;
            if (budget.Exhausted) yield break;
        }
    }

    private sealed class MailReadBudget(int maximum)
    {
        public int Remaining { get; set; } = Math.Max(0, maximum);
        public bool Exhausted { get; set; }
    }

    private static bool TryMoveNext<T>(IEnumerator<T> enumerator, string member, IExtractionIssueSink issues, CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        try { return enumerator.MoveNext(); }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            issues.Report(new ExtractionIssue(member, "mail_folder_failed", ex.Message));
            return false;
        }
    }

    private static OutlookMailMessage ReadStoreMessage(XstMessage message, string id, string folder, string fingerprint)
    {
        var metadata = new MailMessageMetadata(id, Clean(message.Subject),
            Address(message.From, message.Recipients.Sender?.Address),
            Clean(string.Join("; ", message.Recipients.To.Take(1000).Select(recipient => Address(recipient.DisplayName, recipient.Address)))),
            Clean(string.Join("; ", message.Recipients.Cc.Take(1000).Select(recipient => Address(recipient.DisplayName, recipient.Address)))),
            message.Date is { } date ? DateTime.SpecifyKind(date, DateTimeKind.Utc) : null, folder, fingerprint);
        var body = message.Properties[PropertyCanonicalName.PidTagBody]?.Value as string;
        if (string.IsNullOrWhiteSpace(body))
        {
            var html = message.Properties[PropertyCanonicalName.PidTagHtml]?.Value;
            if (html is byte[] bytes)
                body = OutlookMailText.FromHtml((message.Encoding ?? Encoding.UTF8).GetString(bytes));
            else if (html is string text)
                body = OutlookMailText.FromHtml(text);
            else if (message.Body is { } fallback)
                body = fallback.Format == XstMessageBodyFormat.Rtf
                    ? OutlookMailText.FromHtml(RtfPipe.Rtf.ToHtml(fallback.Text ?? string.Empty))
                    : fallback.Text;
        }
        // Read attachment metadata only; the potentially large payloads stay in the store.
        var names = message.Attachments.Where(attachment => attachment.IsFile)
            .Select(attachment => Clean(attachment.DisplayName)).Take(1000).ToArray();
        return new OutlookMailMessage(metadata, body ?? string.Empty, names);
    }

    private static string Address(string? name, string? address) =>
        string.IsNullOrWhiteSpace(address) ? Clean(name) : $"{Clean(name)} <{Clean(address)}>".Trim();

    private static string Clean(string? value) => MarkupText.Normalize(value is null ? string.Empty : value[..Math.Min(value.Length, 4096)]);
}
