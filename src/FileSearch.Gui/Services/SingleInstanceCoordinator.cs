using System;
using System.IO;
using System.IO.Pipes;
using System.Security.Principal;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;

namespace FileSearch.Gui.Services;

internal sealed class SingleInstanceCoordinator : IDisposable
{
    private static readonly string s_instanceKey = BuildInstanceKey();
    private static readonly string s_mutexName = $@"Local\FileSearch.Gui.{s_instanceKey}";
    private static readonly string s_pipeName = $"FileSearch.Gui.{s_instanceKey}";

    private readonly Mutex _mutex;
    private readonly CancellationTokenSource _serverCts = new();
    private Task? _serverTask;
    private bool _disposed;

    public SingleInstanceCoordinator()
    {
        _mutex = new Mutex(initiallyOwned: true, s_mutexName, out var createdNew);
        IsPrimary = createdNew;
    }

    public bool IsPrimary { get; }

    public void StartServer(Action<AppStartupOptions> onActivated)
    {
        if (!IsPrimary || _serverTask is not null)
            return;

        _serverTask = Task.Run(() => RunServerAsync(onActivated, _serverCts.Token));
    }

    public static async Task<bool> TrySendActivationAsync(
        AppStartupOptions options,
        CancellationToken cancellationToken)
    {
        try
        {
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            timeout.CancelAfter(TimeSpan.FromSeconds(2));

            await using var client = new NamedPipeClientStream(
                ".",
                s_pipeName,
                PipeDirection.Out,
                PipeOptions.Asynchronous | PipeOptions.CurrentUserOnly);
            await client.ConnectAsync(timeout.Token).ConfigureAwait(false);

            var payload = JsonSerializer.Serialize(options);
            await using var writer = new StreamWriter(client, Encoding.UTF8, leaveOpen: false)
            {
                AutoFlush = true,
            };
            await writer.WriteAsync(payload).ConfigureAwait(false);
            return true;
        }
        catch
        {
            return false;
        }
    }

    private static async Task RunServerAsync(
        Action<AppStartupOptions> onActivated,
        CancellationToken cancellationToken)
    {
        while (!cancellationToken.IsCancellationRequested)
        {
            try
            {
                await using var server = new NamedPipeServerStream(
                    s_pipeName,
                    PipeDirection.In,
                    maxNumberOfServerInstances: 1,
                    PipeTransmissionMode.Byte,
                    PipeOptions.Asynchronous | PipeOptions.CurrentUserOnly);
                await server.WaitForConnectionAsync(cancellationToken).ConfigureAwait(false);

                using var reader = new StreamReader(server, Encoding.UTF8, leaveOpen: true);
                var payload = await reader.ReadToEndAsync(cancellationToken).ConfigureAwait(false);
                var options = JsonSerializer.Deserialize<AppStartupOptions>(payload) ?? AppStartupOptions.Empty;
                onActivated(options);
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                return;
            }
            catch (ObjectDisposedException) when (cancellationToken.IsCancellationRequested)
            {
                return;
            }
            catch (IOException)
            {
            }
            catch (JsonException)
            {
            }
        }
    }

    private static string BuildInstanceKey()
    {
        try
        {
            var sid = WindowsIdentity.GetCurrent().User?.Value;
            if (!string.IsNullOrWhiteSpace(sid))
                return sid;
        }
        catch
        {
        }

        return Environment.UserName.Replace(' ', '_');
    }

    public void Dispose()
    {
        if (_disposed)
            return;

        _disposed = true;
        _serverCts.Cancel();
        _serverCts.Dispose();

        if (IsPrimary)
        {
            try
            {
                _mutex.ReleaseMutex();
            }
            catch
            {
            }
        }

        _mutex.Dispose();
    }
}
