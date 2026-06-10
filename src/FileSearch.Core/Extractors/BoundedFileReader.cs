using System;
using System.IO;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

namespace FileSearch.Core.Extractors;

/// <summary>
/// Reads at most a fixed number of characters from a text file. Whole-file
/// extractors (EML, RTF) use this so one huge file can't balloon memory;
/// content past the cap simply isn't searched — search is best-effort.
/// </summary>
internal static class BoundedFileReader
{
    public static async Task<string> ReadTextAsync(string path, int maxChars, CancellationToken cancellationToken)
    {
        using var reader = new StreamReader(new FileStream(
            path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite,
            bufferSize: 64 * 1024, useAsync: true));

        var buffer = new char[64 * 1024];
        var builder = new StringBuilder();
        while (builder.Length < maxChars)
        {
            var toRead = Math.Min(buffer.Length, maxChars - builder.Length);
            var read = await reader.ReadAsync(buffer.AsMemory(0, toRead), cancellationToken).ConfigureAwait(false);
            if (read == 0)
                break;

            builder.Append(buffer, 0, read);
        }

        return builder.ToString();
    }
}
