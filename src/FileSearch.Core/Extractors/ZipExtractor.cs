using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
using System.Runtime.CompilerServices;
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
/// small archive that expands enormously (zip bomb) can't stall a worker or
/// balloon memory; excess content is silently truncated — search is
/// best-effort.
/// </summary>
public sealed class ZipExtractor : ITextExtractor
{
    private const long DefaultMaxEntryBytes = 16L * 1024 * 1024;
    private const long DefaultMaxTotalBytes = 64L * 1024 * 1024;
    private const int DefaultMaxEntries = 10_000;

    private readonly long _maxEntryBytes;
    private readonly long _maxTotalBytes;
    private readonly int _maxEntries;

    public ZipExtractor()
        : this(DefaultMaxEntryBytes, DefaultMaxTotalBytes, DefaultMaxEntries)
    {
    }

    /// <summary>Test hook: shrinks the caps so bomb behavior is testable
    /// without generating huge fixtures.</summary>
    internal ZipExtractor(long maxEntryBytes, long maxTotalBytes, int maxEntries)
    {
        _maxEntryBytes = maxEntryBytes;
        _maxTotalBytes = maxTotalBytes;
        _maxEntries = maxEntries;
    }

    public IReadOnlyCollection<string> SupportedExtensions { get; } = new[] { ".zip" };

    public async IAsyncEnumerable<TextLine> ExtractAsync(
        string path,
        [EnumeratorCancellation] CancellationToken cancellationToken)
    {
        using var archive = ZipFile.OpenRead(path);
        int lineNumber = 0;
        int entriesSeen = 0;
        long remainingBudget = _maxTotalBytes;

        foreach (var entry in archive.Entries)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (++entriesSeen > _maxEntries || remainingBudget <= 0)
                yield break;

            if (entry.Length == 0) continue; // directory or empty file
            if (!IsTextEntry(entry.Name)) continue;
            if (entry.Length > _maxEntryBytes) continue; // header says too big

            lineNumber++;
            yield return new TextLine(lineNumber, $"=== {entry.FullName} ===");

            // The cap stream enforces the budget on actual decompressed
            // bytes — entry headers can lie.
            var capped = new CappedStream(entry.Open(), Math.Min(_maxEntryBytes, remainingBudget));
            using (capped)
            using (var reader = new StreamReader(capped, detectEncodingFromByteOrderMarks: true))
            {
                while (await reader.ReadLineAsync(cancellationToken).ConfigureAwait(false) is { } line)
                {
                    lineNumber++;
                    yield return new TextLine(lineNumber, line);
                }
            }

            remainingBudget -= capped.BytesRead;
        }
    }

    private static bool IsTextEntry(string entryName)
    {
        var ext = Path.GetExtension(entryName);
        return !string.IsNullOrEmpty(ext) && TextFileExtensions.All.Contains(ext);
    }

    /// <summary>Truncates reads at a byte budget (reports EOF past it).</summary>
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
                return 0;

            var read = _inner.Read(buffer, offset, toRead);
            _remaining -= read;
            BytesRead += read;
            return read;
        }

        public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
        {
            var toRead = (int)Math.Min(buffer.Length, _remaining);
            if (toRead <= 0)
                return 0;

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
}
