using System;
using System.Buffers.Binary;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
using System.Runtime.CompilerServices;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

namespace FileSearch.Core.Extractors;

/// <summary>
/// Reads bounded text, PDF, and supported Office members inside ZIP archives.
/// New document parsers run in the isolated host; nested archives stay disabled.
/// Each entry is preceded by a "=== entry/path ===" marker line so callers
/// can identify which archive member matched.
/// Decompression is capped per entry, per archive, and by entry count so a
/// small archive that expands enormously (zip bomb) cannot stall a worker or
/// balloon memory; excess content is best-effort and reported as an issue.
/// </summary>
public sealed partial class ZipExtractor : IContextualDiagnosticTextExtractor
{
    public const int MaxDocumentTextCharacters = 2 * 1024 * 1024;
    public const int MaxDocumentTextLines = 50_000;
    public string ExtractorId => "filesearch.zip";

    public string ExtractorVersion => "5";

    private readonly ZipArchivePolicy _policy;
    private readonly IEmbeddedImageOcrService _embeddedImageOcr;

    public ZipExtractor()
        : this(ZipArchivePolicy.Default, null)
    {
    }

    public ZipExtractor(IEmbeddedImageOcrService? embeddedImageOcr)
        : this(ZipArchivePolicy.Default, embeddedImageOcr)
    {
    }

    /// <summary>Test hook: shrinks the caps so bomb behavior is testable
    /// without generating huge fixtures.</summary>
    internal ZipExtractor(long maxEntryBytes, long maxTotalBytes, int maxEntries)
        : this(new ZipArchivePolicy(maxEntryBytes, maxTotalBytes, maxEntries), null)
    {
    }

    internal ZipExtractor(ZipArchivePolicy policy, IEmbeddedImageOcrService? embeddedImageOcr = null)
        : this(policy, embeddedImageOcr, null, null, false, null)
    {
    }

    public ZipExtractor(IEmbeddedImageOcrService? embeddedImageOcr,
        Func<IExtractorRegistry> documents, IOutOfProcessExtractionService host)
        : this(ZipArchivePolicy.Default, embeddedImageOcr, documents, host, false, null)
    {
    }

    public static ZipExtractor CreateForIsolatedHost(string temporaryDirectory) =>
        new(ZipArchivePolicy.Default, null, null, null, true,
            Path.GetFullPath(temporaryDirectory ?? throw new ArgumentNullException(nameof(temporaryDirectory))));

    internal ZipExtractor(ZipArchivePolicy policy, Func<IExtractorRegistry> documents, string temporaryDirectory)
        : this(policy, null, documents, null, true, Path.GetFullPath(temporaryDirectory))
    {
    }

    private ZipExtractor(ZipArchivePolicy policy, IEmbeddedImageOcrService? embeddedImageOcr,
        Func<IExtractorRegistry>? documents, IOutOfProcessExtractionService? host,
        bool allowDirect, string? temporaryDirectory)
    {
        _policy = policy;
        _embeddedImageOcr = embeddedImageOcr ?? new NullEmbeddedImageOcrService();
        _documents = documents ?? (() => BuiltInDocuments.Value);
        _documentHost = host;
        _allowDirectDocumentParsing = allowDirect;
        _temporaryDirectory = temporaryDirectory;
    }

    public IReadOnlyCollection<string> SupportedExtensions { get; } = new[] { ".zip" };

    public async IAsyncEnumerable<TextLine> ExtractAsync(
        string path,
        [EnumeratorCancellation] CancellationToken cancellationToken)
    {
        await foreach (var line in ExtractAsync(path, NullExtractionIssueSink.Instance, cancellationToken).ConfigureAwait(false))
            yield return line;
    }

    public async IAsyncEnumerable<TextLine> ExtractAsync(
        string path,
        IExtractionIssueSink issues,
        [EnumeratorCancellation] CancellationToken cancellationToken)
    {
        await foreach (var line in ExtractAsync(path, new TextExtractionContext(), issues, cancellationToken).ConfigureAwait(false))
            yield return line;
    }

    public async IAsyncEnumerable<TextLine> ExtractAsync(
        string path,
        TextExtractionContext context,
        [EnumeratorCancellation] CancellationToken cancellationToken)
    {
        await foreach (var line in ExtractAsync(path, context, NullExtractionIssueSink.Instance, cancellationToken).ConfigureAwait(false))
            yield return line;
    }

    public async IAsyncEnumerable<TextLine> ExtractAsync(
        string path,
        TextExtractionContext context,
        IExtractionIssueSink issues,
        [EnumeratorCancellation] CancellationToken cancellationToken)
    {
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        deadline.CancelAfter(_policy.MaxDuration);
        await using var enumerator = ExtractCoreAsync(path, context, issues, deadline.Token).GetAsyncEnumerator(deadline.Token);
        while (true)
        {
            bool next;
            try { next = await enumerator.MoveNextAsync().ConfigureAwait(false); }
            catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested && deadline.IsCancellationRequested)
            {
                issues.Report(new ExtractionIssue(null, "archive_time_limit", "Archive search stopped at its duration limit; coverage is partial."));
                yield break;
            }
            if (!next) yield break;
            yield return enumerator.Current;
        }
    }

    private async IAsyncEnumerable<TextLine> ExtractCoreAsync(
        string path, TextExtractionContext context, IExtractionIssueSink issues,
        [EnumeratorCancellation] CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var entryMetadata = ReadEntryMetadata(path);
        using var archive = ZipFile.OpenRead(path);
        var info = new FileInfo(path);
        var fingerprint = FormattableString.Invariant($"{info.Length}:{info.LastWriteTimeUtc.Ticks}");
        var duplicateNames = archive.Entries.GroupBy(entry => entry.FullName, StringComparer.Ordinal)
            .Where(group => group.Skip(1).Any()).Select(group => group.Key).ToHashSet(StringComparer.Ordinal);
        using var ownedHost = _documentHost is null && !_allowDirectDocumentParsing
            ? new OutOfProcessExtractionService(new OutOfProcessExtractionOptions { UseReusableHostPool = false, HostPoolSize = 1 })
            : null;
        var documentHost = _documentHost ?? ownedHost;
        int lineNumber = 0;
        int entriesSeen = 0;
        long remainingBudget = _policy.MaxTotalBytes;
        int remainingText = _policy.MaxTotalTextCharacters;
        int remainingLines = _policy.MaxTotalTextLines;

        foreach (var entry in archive.Entries)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (++entriesSeen > _policy.MaxEntries)
            {
                ReportIssue(issues, entry, "archive_max_entries_exceeded", "Archive member skipped because the archive entry limit was reached.");
                yield break;
            }

            if (remainingBudget <= 0)
            {
                ReportIssue(issues, entry, "archive_total_bytes_exceeded", "Archive member skipped because the archive extraction byte budget was exhausted.");
                yield break;
            }

            if (remainingText <= 0 || remainingLines <= 0)
            {
                ReportIssue(issues, entry, "archive_text_limit", "Archive search stopped at its text or line budget; coverage is partial.");
                yield break;
            }

            if (!IsSafeMember(entry.FullName))
            {
                ReportIssue(issues, entry, "archive_member_unsafe_path", "Archive member skipped because its path is unsafe or longer than 512 characters.");
                continue;
            }
            if (duplicateNames.Contains(entry.FullName))
            {
                ReportIssue(issues, entry, "archive_member_duplicate_name", "Archive member skipped because its exact name is duplicated and cannot identify one member safely.");
                continue;
            }

            if (entry.Length == 0)
                continue;

            entryMetadata.TryGetValue(entry.FullName, out var metadata);
            if (metadata?.IsEncrypted == true)
            {
                ReportIssue(issues, entry, "archive_member_encrypted", "Archive member skipped because encrypted archive entries are not supported.");
                continue;
            }

            if (metadata is not null && !IsSupportedCompressionMethod(metadata.CompressionMethod))
            {
                ReportIssue(issues, entry, "archive_member_unsupported_compression", $"Archive member skipped because compression method {metadata.CompressionMethod} is not supported.");
                continue;
            }

            if (_policy.MaxNestedArchiveDepth == 0 && IsArchiveEntry(entry.Name))
            {
                ReportIssue(issues, entry, "archive_member_nested_archive_disabled", "Archive member skipped because nested archive extraction depth is 0.");
                continue;
            }

            var isImageEntry = IsImageEntry(entry.Name);
            var isDocumentEntry = IsDocumentEntry(entry.Name);
            if (!isDocumentEntry && !IsTextEntry(entry.Name) && (!context.EnableOcr || !isImageEntry))
            {
                ReportIssue(issues, entry, "archive_member_unsupported_type", "Archive member skipped because its file type is not indexed inside archives.");
                continue;
            }

            if (entry.Length > _policy.MaxEntryBytes)
            {
                ReportIssue(issues, entry, "archive_member_too_large", "Archive member skipped because its declared uncompressed size exceeds the per-entry limit.");
                continue;
            }

            var entryBudget = Math.Min(_policy.MaxEntryBytes, remainingBudget);
            var likelyTruncated = entry.Length > entryBudget;
            int entryText = _policy.MaxEntryTextCharacters;
            int entryLines = _policy.MaxEntryTextLines;
            bool textLimitReported = false;
            bool TakeOutput(TextLine candidate, out TextLine bounded)
            {
                bounded = candidate;
                int overhead = entry.FullName.Length + (candidate.Anchor?.DisplayText.Length ?? 0) +
                    (candidate.Anchor?.ArchiveContentAnchor?.DisplayText.Length ?? 0) + 256;
                int allowed = Math.Min(entryText, remainingText) - overhead;
                if (allowed < 0 || entryLines <= 0 || remainingLines <= 0)
                {
                    if (!textLimitReported)
                        ReportIssue(issues, entry, "archive_member_text_limit", "Archive member text or metadata budget was reached; coverage is partial.");
                    textLimitReported = true;
                    return false;
                }
                var content = candidate.Content;
                if (content.Length > allowed)
                {
                    content = content[..allowed];
                    if (!textLimitReported)
                        ReportIssue(issues, entry, "archive_member_text_limit", "Archive member text was truncated at its output budget; coverage is partial.");
                    textLimitReported = true;
                }
                int charge = content.Length + overhead;
                entryText -= charge;
                remainingText -= charge;
                entryLines--;
                remainingLines--;
                bounded = candidate with { Number = ++lineNumber, Content = content };
                return true;
            }

            if (isDocumentEntry)
            {
                BinaryEntryReadResult documentResult;
                try { documentResult = await ReadBinaryEntryAsync(entry, entryBudget, cancellationToken).ConfigureAwait(false); }
                catch (Exception ex) when (ex is IOException or InvalidDataException or UnauthorizedAccessException)
                {
                    ReportIssue(issues, entry, "archive_member_unreadable", $"Archive document could not be read: {ex.Message}");
                    continue;
                }
                remainingBudget -= documentResult.BytesRead;
                if (likelyTruncated || documentResult.Truncated)
                {
                    ReportIssue(issues, entry, "archive_member_truncated", "Archive document skipped because complete bytes did not fit the extraction budget.");
                    continue;
                }
                var package = await CheckDocumentPackageAsync(documentResult.Bytes, entry.Name,
                    Math.Max(0, remainingBudget), cancellationToken).ConfigureAwait(false);
                remainingBudget -= package.BytesRead;
                if (!package.Allowed)
                {
                    ReportIssue(issues, entry, package.Code, package.Message);
                    continue;
                }
                var documentLines = await ReadDocumentAsync(entry, documentResult.Bytes, documentHost,
                    Math.Min(entryText, remainingText), issues, cancellationToken).ConfigureAwait(false);
                if (documentLines.Count == 0) continue;
                var marker = new TextLine(0, $"=== {entry.FullName} ===", ArchiveAnchor(entry.FullName, fingerprint, null));
                if (TakeOutput(marker, out var markerLine)) yield return markerLine;
                foreach (var line in documentLines)
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    var wrapped = line with { Anchor = ArchiveAnchor(entry.FullName, fingerprint, DocumentAnchor(line, entry.Name)) };
                    if (!TakeOutput(wrapped, out var bounded)) break;
                    yield return bounded;
                }
            }
            else if (context.EnableOcr && isImageEntry)
            {
                BinaryEntryReadResult imageResult;
                try
                {
                    imageResult = await ReadBinaryEntryAsync(entry, entryBudget, cancellationToken).ConfigureAwait(false);
                }
                catch (Exception ex) when (ex is IOException or InvalidDataException or UnauthorizedAccessException)
                {
                    ReportIssue(issues, entry, "archive_member_unreadable", $"Archive member skipped because it could not be read: {ex.Message}");
                    continue;
                }

                remainingBudget -= imageResult.BytesRead;
                if (likelyTruncated || imageResult.Truncated)
                    ReportIssue(issues, entry, "archive_member_truncated", "Archive member content was truncated because the archive byte budget was exhausted.");

                var request = new EmbeddedImageOcrRequest(
                    SourceAnchorKind.Archive,
                    entry.FullName,
                    $"archive member {entry.FullName}",
                    Section: entry.FullName);
                await foreach (var line in _embeddedImageOcr.ExtractAsync(imageResult.Bytes, request, cancellationToken).ConfigureAwait(false))
                {
                    var inner = line.Anchor is null ? null : line.Anchor with { Kind = SourceAnchorKind.ImageOcr, MemberPath = null };
                    if (!TakeOutput(line with { Anchor = ArchiveAnchor(entry.FullName, fingerprint, inner) }, out var bounded)) break;
                    yield return bounded;
                }
            }
            else
            {
                EntryReadResult entryResult;
                try
                {
                    entryResult = await ReadTextEntryAsync(entry, entryBudget, cancellationToken).ConfigureAwait(false);
                }
                catch (Exception ex) when (ex is IOException or InvalidDataException or UnauthorizedAccessException)
                {
                    ReportIssue(issues, entry, "archive_member_unreadable", $"Archive member skipped because it could not be read: {ex.Message}");
                    continue;
                }

                remainingBudget -= entryResult.BytesRead;
                if (likelyTruncated || entryResult.Truncated)
                    ReportIssue(issues, entry, "archive_member_truncated", "Archive member content was truncated because the archive byte budget was exhausted.");

                var marker = new TextLine(0, $"=== {entry.FullName} ===", ArchiveAnchor(entry.FullName, fingerprint, null));
                if (TakeOutput(marker, out var markerLine)) yield return markerLine;

                int memberLine = 0;
                foreach (var line in entryResult.Lines)
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    var inner = new SourceAnchor(SourceAnchorKind.Text, $"line {++memberLine}", Line: memberLine);
                    if (!TakeOutput(new TextLine(memberLine, line, ArchiveAnchor(entry.FullName, fingerprint, inner)), out var bounded)) break;
                    yield return bounded;
                }
            }
        }
    }

    private static async Task<EntryReadResult> ReadTextEntryAsync(
        ZipArchiveEntry entry,
        long byteBudget,
        CancellationToken cancellationToken)
    {
        var capped = new CappedStream(entry.Open(), byteBudget);
        using (capped)
        using (var reader = new StreamReader(capped, detectEncodingFromByteOrderMarks: true))
        {
            var lines = new List<string>();
            while (await reader.ReadLineAsync(cancellationToken).ConfigureAwait(false) is { } line)
                lines.Add(line);

            return new EntryReadResult(lines, capped.BytesRead, capped.LimitReached);
        }
    }

    private sealed record EntryReadResult(IReadOnlyList<string> Lines, long BytesRead, bool Truncated);

    private static async Task<BinaryEntryReadResult> ReadBinaryEntryAsync(
        ZipArchiveEntry entry,
        long byteBudget,
        CancellationToken cancellationToken)
    {
        using (var stream = entry.Open())
        using (var memory = new MemoryStream())
        {
            var buffer = new byte[8192];
            while (memory.Length < byteBudget)
            {
                int count = await stream.ReadAsync(buffer.AsMemory(0, (int)Math.Min(buffer.Length, byteBudget - memory.Length)), cancellationToken).ConfigureAwait(false);
                if (count == 0) return new BinaryEntryReadResult(memory.ToArray(), memory.Length, false);
                memory.Write(buffer, 0, count);
            }
            bool truncated = await stream.ReadAsync(buffer.AsMemory(0, 1), cancellationToken).ConfigureAwait(false) != 0;
            return new BinaryEntryReadResult(memory.ToArray(), memory.Length, truncated);
        }
    }

    private sealed record BinaryEntryReadResult(byte[] Bytes, long BytesRead, bool Truncated);

    private static void ReportIssue(
        IExtractionIssueSink issues,
        ZipArchiveEntry entry,
        string code,
        string message)
    {
        string member = entry.FullName.Length <= 512 ? entry.FullName : entry.FullName[..512];
        issues.Report(new ExtractionIssue(member, code, message.Length <= 1000 ? message : message[..1000]));
    }

    private static bool IsTextEntry(string entryName)
    {
        var ext = Path.GetExtension(entryName);
        return !string.IsNullOrEmpty(ext) && TextFileExtensions.All.Contains(ext);
    }

    private static bool IsArchiveEntry(string entryName)
    {
        var ext = Path.GetExtension(entryName);
        return !string.IsNullOrEmpty(ext) && ArchiveExtensions.Contains(ext);
    }

    private static bool IsImageEntry(string entryName)
    {
        var ext = Path.GetExtension(entryName);
        return !string.IsNullOrEmpty(ext) && ImageExtensions.Contains(ext);
    }

    private static bool IsSupportedCompressionMethod(ushort compressionMethod) =>
        compressionMethod is 0 or 8;

    private static Dictionary<string, ZipEntryMetadata> ReadEntryMetadata(string path)
    {
        try
        {
            using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
            return ZipCentralDirectoryReader.Read(stream);
        }
        catch (Exception ex) when (ex is IOException or InvalidDataException or UnauthorizedAccessException)
        {
            return new Dictionary<string, ZipEntryMetadata>(StringComparer.Ordinal);
        }
    }

    private static readonly HashSet<string> ArchiveExtensions = new(StringComparer.OrdinalIgnoreCase)
    {
        ".zip", ".zipx", ".jar", ".war", ".ear", ".nupkg", ".vsix",
        ".7z", ".rar", ".tar", ".tgz", ".gz", ".bz2", ".xz",
    };

    private static readonly HashSet<string> ImageExtensions = new(StringComparer.OrdinalIgnoreCase)
    {
        ".png", ".jpg", ".jpeg", ".bmp", ".tif", ".tiff",
    };

    /// <summary>Truncates reads at a byte budget and reports EOF past it.</summary>
    private sealed class CappedStream : Stream
    {
        private readonly Stream _inner;
        private long _remaining;

        public CappedStream(Stream inner, long cap)
        {
            _inner = inner;
            _remaining = cap;
        }

        public long BytesRead { get; private set; }

        public bool LimitReached { get; private set; }

        public override bool CanRead => true;

        public override bool CanSeek => false;

        public override bool CanWrite => false;

        public override long Length => throw new NotSupportedException();

        public override long Position
        {
            get => throw new NotSupportedException();
            set => throw new NotSupportedException();
        }

        public override int Read(byte[] buffer, int offset, int count)
        {
            var toRead = (int)Math.Min(count, _remaining);
            if (toRead <= 0)
            {
                LimitReached = true;
                return 0;
            }

            var read = _inner.Read(buffer, offset, toRead);
            _remaining -= read;
            BytesRead += read;
            return read;
        }

        public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
        {
            var toRead = (int)Math.Min(buffer.Length, _remaining);
            if (toRead <= 0)
            {
                LimitReached = true;
                return 0;
            }

            var read = await _inner.ReadAsync(buffer[..toRead], cancellationToken).ConfigureAwait(false);
            _remaining -= read;
            BytesRead += read;
            return read;
        }

        public override Task<int> ReadAsync(byte[] buffer, int offset, int count, CancellationToken cancellationToken) =>
            ReadAsync(buffer.AsMemory(offset, count), cancellationToken).AsTask();

        public override void Flush()
        {
        }

        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();

        public override void SetLength(long value) => throw new NotSupportedException();

        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();

        protected override void Dispose(bool disposing)
        {
            if (disposing)
                _inner.Dispose();

            base.Dispose(disposing);
        }
    }

    private sealed record ZipEntryMetadata(bool IsEncrypted, ushort CompressionMethod);

    private static class ZipCentralDirectoryReader
    {
        private const uint EndOfCentralDirectorySignature = 0x06054b50;
        private const uint CentralDirectoryFileHeaderSignature = 0x02014b50;
        private const ushort Zip64EntryCountOrOffset = 0xffff;
        private const uint Zip64SizeOrOffset = 0xffffffff;
        private const int EndOfCentralDirectoryMinLength = 22;
        private const int MaxCommentLength = 65_535;

        public static Dictionary<string, ZipEntryMetadata> Read(Stream stream)
        {
            if (!stream.CanSeek || stream.Length < EndOfCentralDirectoryMinLength)
                return new Dictionary<string, ZipEntryMetadata>(StringComparer.Ordinal);

            var eocd = FindEndOfCentralDirectory(stream);
            if (eocd is null)
                return new Dictionary<string, ZipEntryMetadata>(StringComparer.Ordinal);

            var totalEntries = ReadUInt16(eocd, 10);
            var centralDirectorySize = ReadUInt32(eocd, 12);
            var centralDirectoryOffset = ReadUInt32(eocd, 16);
            if (totalEntries == Zip64EntryCountOrOffset ||
                centralDirectorySize == Zip64SizeOrOffset ||
                centralDirectoryOffset == Zip64SizeOrOffset ||
                (long)centralDirectoryOffset + centralDirectorySize > stream.Length)
            {
                return new Dictionary<string, ZipEntryMetadata>(StringComparer.Ordinal);
            }

            var metadata = new Dictionary<string, ZipEntryMetadata>(StringComparer.Ordinal);
            stream.Seek(centralDirectoryOffset, SeekOrigin.Begin);
            var header = new byte[46];
            for (var i = 0; i < totalEntries; i++)
            {
                stream.ReadExactly(header);
                if (ReadUInt32(header, 0) != CentralDirectoryFileHeaderSignature)
                    break;

                var flags = ReadUInt16(header, 8);
                var compressionMethod = ReadUInt16(header, 10);
                var nameLength = ReadUInt16(header, 28);
                var extraLength = ReadUInt16(header, 30);
                var commentLength = ReadUInt16(header, 32);

                var nameBytes = new byte[nameLength];
                stream.ReadExactly(nameBytes);
                var name = DecodeEntryName(nameBytes, flags);
                if (!string.IsNullOrEmpty(name))
                {
                    metadata[name] = new ZipEntryMetadata(
                        IsEncrypted: (flags & 0x0001) != 0,
                        CompressionMethod: compressionMethod);
                }

                if (extraLength + commentLength > 0)
                    stream.Seek(extraLength + commentLength, SeekOrigin.Current);
            }

            return metadata;
        }

        private static byte[]? FindEndOfCentralDirectory(Stream stream)
        {
            var readLength = (int)Math.Min(stream.Length, EndOfCentralDirectoryMinLength + MaxCommentLength);
            var buffer = new byte[readLength];
            stream.Seek(stream.Length - readLength, SeekOrigin.Begin);
            stream.ReadExactly(buffer);

            for (var i = buffer.Length - EndOfCentralDirectoryMinLength; i >= 0; i--)
            {
                if (ReadUInt32(buffer.AsSpan(i), 0) == EndOfCentralDirectorySignature)
                    return buffer[i..];
            }

            return null;
        }

        private static string DecodeEntryName(byte[] nameBytes, ushort flags) =>
            (flags & 0x0800) != 0
                ? Encoding.UTF8.GetString(nameBytes)
                : Encoding.UTF8.GetString(nameBytes);

        private static ushort ReadUInt16(ReadOnlySpan<byte> bytes, int offset) =>
            BinaryPrimitives.ReadUInt16LittleEndian(bytes[offset..]);

        private static uint ReadUInt32(ReadOnlySpan<byte> bytes, int offset) =>
            BinaryPrimitives.ReadUInt32LittleEndian(bytes[offset..]);
    }
}
