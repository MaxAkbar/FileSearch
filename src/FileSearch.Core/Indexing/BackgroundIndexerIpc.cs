using System;
using System.IO;
using System.IO.Pipes;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Threading;
using System.Threading.Tasks;
using FileSearch.Core.Walker;

namespace FileSearch.Core.Indexing;

public enum BackgroundIndexerCommand
{
    Ping = 1,
    GetStatus = 2,
    Pause = 3,
    Resume = 4,
    Shutdown = 5,
    AddOrUpdateLocation = 6,
    RemoveLocation = 7,
    RefreshRoot = 8,
    SetResourceProfile = 9,
    SetRuntimeOptions = 10,
    SetForegroundSearchActive = 11,
    CompactDatabase = 12,
    QueueRootRefresh = 13,
    ValidateRoot = 14,
}

public sealed record BackgroundIndexedLocation(
    string Root,
    bool WatchEnabled,
    bool Recursive,
    bool IncludeHidden,
    string[] IncludeExtensions,
    string[] ExcludeExtensions,
    string[] IncludeDirectories,
    string[] ExcludeDirectories)
{
    public static BackgroundIndexedLocation FromIndexedLocation(IndexedLocation location) =>
        new(
            IndexPath.NormalizeRoot(location.Root),
            location.WatchEnabled,
            location.WalkerOptions.Recursive,
            location.WalkerOptions.IncludeHidden,
            location.WalkerOptions.IncludeExtensions.ToArray(),
            location.WalkerOptions.ExcludeExtensions.ToArray(),
            location.WalkerOptions.IncludeDirectories.ToArray(),
            location.WalkerOptions.ExcludeDirectories.ToArray());

    public IndexedLocation ToIndexedLocation() =>
        new(
            IndexPath.NormalizeRoot(Root),
            new WalkerOptions
            {
                IncludeGlobs = Array.Empty<string>(),
                ExcludeGlobs = Array.Empty<string>(),
                IncludeExtensions = IncludeExtensions.ToHashSet(StringComparer.OrdinalIgnoreCase),
                ExcludeExtensions = ExcludeExtensions.ToHashSet(StringComparer.OrdinalIgnoreCase),
                IncludeDirectories = IncludeDirectories.ToHashSet(StringComparer.OrdinalIgnoreCase),
                ExcludeDirectories = ExcludeDirectories.ToHashSet(StringComparer.OrdinalIgnoreCase),
                Recursive = Recursive,
                IncludeHidden = IncludeHidden,
                MinFileSizeBytes = 0,
                MaxFileSizeBytes = 0,
                ModifiedAfterUtc = null,
                ModifiedBeforeUtc = null,
            },
            WatchEnabled);
}

public sealed record BackgroundIndexerRequest(
    BackgroundIndexerCommand Command,
    BackgroundIndexedLocation? Location = null,
    string? Root = null,
    IndexerResourceProfile? ResourceProfile = null,
    IndexerRuntimeOptions? RuntimeOptions = null,
    bool? ForegroundSearchActive = null,
    IndexQueuePriority? Priority = null);

public sealed record BackgroundIndexerResponse(
    bool Success,
    string Message,
    IndexingStatus? Status = null,
    IndexValidationResult? ValidationResult = null);

public static class BackgroundIndexerEndpoint
{
    public static string PipeName { get; } = $"FileSearch.Indexer.{BuildUserKey()}";

    private static string BuildUserKey()
    {
        var identity = $"{Environment.UserDomainName}\\{Environment.UserName}";
        var hash = SHA256.HashData(Encoding.UTF8.GetBytes(identity));
        return Convert.ToHexString(hash[..8]);
    }
}

public static class BackgroundIndexerClient
{
    private static readonly JsonSerializerOptions s_jsonOptions = new()
    {
        Converters = { new JsonStringEnumConverter() },
    };

    public static async Task<BackgroundIndexerResponse?> TrySendAsync(
        BackgroundIndexerRequest request,
        TimeSpan timeout,
        CancellationToken cancellationToken)
    {
        try
        {
            using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            timeoutCts.CancelAfter(timeout);

            await using var pipe = new NamedPipeClientStream(
                ".",
                BackgroundIndexerEndpoint.PipeName,
                PipeDirection.InOut,
                PipeOptions.Asynchronous | PipeOptions.CurrentUserOnly);
            await pipe.ConnectAsync(timeoutCts.Token).ConfigureAwait(false);

            await using var writer = new StreamWriter(pipe, Encoding.UTF8, leaveOpen: true)
            {
                AutoFlush = true,
            };
            using var reader = new StreamReader(pipe, Encoding.UTF8, leaveOpen: true);

            var payload = JsonSerializer.Serialize(request, s_jsonOptions);
            await writer.WriteLineAsync(payload).ConfigureAwait(false);

            var responsePayload = await reader.ReadLineAsync(timeoutCts.Token).ConfigureAwait(false);
            return string.IsNullOrWhiteSpace(responsePayload)
                ? null
                : JsonSerializer.Deserialize<BackgroundIndexerResponse>(responsePayload, s_jsonOptions);
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            return null;
        }
        catch (IOException)
        {
            return null;
        }
        catch (UnauthorizedAccessException)
        {
            return null;
        }
        catch (JsonException)
        {
            return null;
        }
    }
}
