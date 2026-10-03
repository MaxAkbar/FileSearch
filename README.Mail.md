# Local Outlook mail search

FileSearch searches `.msg` messages and messages inside local `.pst` and `.ost` stores without installing Outlook or an Outlook IFilter. Existing `.eml` support remains available. Mail is read locally; FileSearch does not sign into Outlook, Exchange, or Microsoft 365.

## Search a mail folder

1. Close Outlook and select the folder containing your MSG, PST, or OST files. A stable copy of a store is also suitable.
2. Enable document extraction and choose **Content** as the search target.
3. Search for a word from a subject, sender, recipient, message body, attachment name, or mail folder. In Unified query mode, `type:email invoice` restricts results to email formats.
4. For repeated searches, add the containing folder as an indexed location with document extraction enabled and build its content index. Quick Search can find these messages when its indexed-content option is enabled.

The whole-drive file-name index finds store filenames. Message content requires the folder content index or a live content search.

CLI examples:

```powershell
.\FileSearch.Cli.exe search "type:email invoice" --path "C:\Mail" --mode unified --json
.\FileSearch.Cli.exe index build "C:\Mail" --json
.\FileSearch.Cli.exe search "type:email invoice" --path "C:\Mail" --mode unified --index --json
```

JSON and JSON Lines include `MailMessage` metadata for each mail hit: message identity, subject, sender, recipients, UTC date, folder, and store fingerprint. CSV adds mail identity, subject, sender, folder, and date columns. Lines in a store have globally unique numbers, while hits retain the identity of their individual message.

## Results and opening messages

Each matching store message has its own result card, even when subjects are identical. Cards show the subject, sender, date, and folder. Previews and **Copy file content** read only the selected message. Size and modified-date filters still describe the physical MSG/PST/OST file; `from:` and `subject:` field operators and message-date filters are not implemented.

Opening a MSG uses its registered application. Opening a PST/OST result creates a **text-only EML copy** of that message and opens it in the registered mail application. It includes readable headers, body, and attachment names, without attachment files or original formatting. Copies are stored in `%TEMP%\FileSearch\MailPreview`; when another message is opened, FileSearch attempts to remove its own copies older than one day. Copies can remain if they are open in another app or FileSearch is no longer used. Delete that directory manually to clear them immediately.

Individual store messages cannot be renamed, deleted, pinned, favorited, or dragged as physical files. **Copy path** and **Reveal in Explorer** refer to the containing store. An indexed message identity is valid only while its store's size and modification timestamp are unchanged. Refresh the index or rerun the search when FileSearch reports that the store changed.

## Coverage and limits

- Extracts subject, sender, To/Cc recipients, date, folder, plain/HTML/RTF body text, and file attachment names. Attachment contents, embedded messages, OCR of mail attachments, and original attachment export are not included.
- OST search sees only items present in the local cache. Online-only or unsynchronized mail is unavailable. No automatic Outlook-profile discovery or mailbox synchronization is performed.
- Store changes trigger extraction of the whole changed store through the existing file indexer. There is no per-message incremental update yet; large active stores are best searched through a closed, stable copy.
- Default emitted-text limits are 2,097,152 characters per message, a 67,108,864-character store budget that also counts repeated message metadata, and 100,000 attempted messages per store (including unreadable messages). Truncation and unreadable messages/folders produce extraction warnings. These limits bound output; they are not a guarantee of parser memory usage.
- MSG property reads and decompressed RTF are limited to 4 MiB. Metadata fields are bounded to 4,096 characters; recipient/attachment enumeration is bounded. Store folder nesting is limited to 64 levels. Oversized MSG properties fail extraction rather than allocating arbitrary amounts of memory.
- The default 100 MiB physical-file cap exempts PST/OST. An explicitly entered GUI maximum or CLI `--max-size` applies to stores as well. Document-host extraction has a five-minute store timeout. Locked, corrupt, or unsupported stores can fail; inspect status messages and index failures.
- Regression fixtures cover a Unicode PST with four body-format variants, a real OST with 92 items, a real MSG, and generated Unicode/ANSI/HTML/RTF messages. This does not certify every historical PST/OST variant or very large enterprise archive.

The index contains local searchable mail text and metadata. See [Privacy](PRIVACY.md) for clearing stored content and preview copies.

## Reader implementation

MSG uses OpenMcdf to read the MAPI streams described by Microsoft's [MS-OXMSG specification](https://learn.microsoft.com/en-us/openspecs/exchange_server_protocols/ms-oxmsg/b046868c-9fbf-41ae-9ffb-8de2bd4eec82), with bounded [MS-OXRTFCP decompression](https://learn.microsoft.com/en-us/openspecs/exchange_server_protocols/ms-oxrtfcp/592e0b55-8b86-45ae-b9d1-b4830e2969ea). PST/OST use XstReader.Api; RtfPipe converts RTF to text through HTML. Package versions, licenses, and source links are recorded in [third-party notices](THIRD-PARTY-NOTICES.md).

XstReader.Api 1.0.6 has a cache-cleanup behavior that can reopen a disposed store. The adapter detaches the cached root before disposal and has exclusive-file-open regression tests for complete, early-stop, and cancelled extraction. Review that workaround before upgrading the pinned reader.
