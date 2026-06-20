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
/// Reads text-like entries inside ZIP archives. v1 limitation: only entries
/// whose names look like text files (by extension) are searched; nested
/// archives, PDFs, or Office docs inside a zip are skipped.
/// Each entry is preceded by a "=== entry/path ===" marker line so callers
/// can identify which archive member matched.
/// Decompression is capped per entry, per archive, and by entry count so a
/// small archive that expands enormously (zip bomb) cannot stall a worker or
/// balloon memory; excess content is best-effort and reported as an issue.
/// </summary>
public sealed class ZipExtractor : IContextualDiagnosticTextExtractor
{
    public string ExtractorId => "filesearch.zip";

    public string ExtractorVersion => "3";

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
    {
        _policy = policy;
        _embeddedImageOcr = embeddedImageOcr ?? new NullEmbeddedImageOcrService();
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
        var entryMetadata = ReadEntryMetadata(path);
        using var archive = ZipFile.OpenRead(path);
        int lineNumber = 0;
        int entriesSeen = 0;
        long remainingBudget = _policy.MaxTotalBytes;

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
            if (!IsTextEntry(entry.Name) && (!context.EnableOcr || !isImageEntry))
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
            if (context.EnableOcr && isImageEntry)
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
                    yield return line with { Number = ++lineNumber };
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

                lineNumber++;
                yield return new TextLine(lineNumber, $"=== {entry.FullName} ===");

                foreach (var line in entryResult.Lines)
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    lineNumber++;
                    yield return new TextLine(lineNumber, line);
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
        var capped = new CappedStream(entry.Open(), byteBudget);
        using (capped)
        using (var memory = new MemoryStream())
        {
            await capped.CopyToAsync(memory, cancellationToken).ConfigureAwait(false);
            return new BinaryEntryReadResult(memory.ToArray(), capped.BytesRead, capped.LimitReached);
        }
    }

    private sealed record BinaryEntryReadResult(byte[] Bytes, long BytesRead, bool Truncated);

    private static void ReportIssue(
        IExtractionIssueSink issues,
        ZipArchiveEntry entry,
        string code,
        string message)
    {
        issues.Report(new ExtractionIssue(entry.FullName, code, message));
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
