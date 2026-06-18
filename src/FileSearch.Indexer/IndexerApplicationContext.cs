using System.Diagnostics;
using System.Drawing;
using System.IO.Pipes;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using FileSearch.Core;
using FileSearch.Core.Indexing;
using FileSearch.Core.Logging;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Forms = System.Windows.Forms;

namespace FileSearch.Indexer;

internal sealed class IndexerApplicationContext : Forms.ApplicationContext
{
    private static readonly JsonSerializerOptions s_jsonOptions = new()
    {
        Converters = { new JsonStringEnumConverter() },
    };

    private readonly CancellationTokenSource _cts = new();
    private readonly SynchronizationContext _uiContext;
    private readonly IHost _host;
    private readonly IIndexingService _indexingService;
    private readonly IFileIndex _fileIndex;
    private readonly Forms.NotifyIcon _notifyIcon;
    private readonly Icon _icon;
    private readonly Forms.ToolStripMenuItem _pauseMenuItem;
    private readonly Forms.ToolStripMenuItem _resumeMenuItem;
    private readonly Task _startupTask;
    private int _stopping;

    public IndexerApplicationContext(IReadOnlyList<string> args)
    {
        _uiContext = SynchronizationContext.Current ?? new SynchronizationContext();
        _host = BuildHost();
        _indexingService = _host.Services.GetRequiredService<IIndexingService>();
        _fileIndex = _host.Services.GetRequiredService<IFileIndex>();
        _indexingService.StatusChanged += OnIndexingStatusChanged;

        _icon = LoadTrayIconImage();
        var menu = new Forms.ContextMenuStrip();
        menu.Items.Add("Show FileSearch", null, (_, _) => ShowFileSearch());
        menu.Items.Add(new Forms.ToolStripSeparator());
        _pauseMenuItem = new Forms.ToolStripMenuItem("Pause indexing", null, (_, _) => _indexingService.Pause());
        _resumeMenuItem = new Forms.ToolStripMenuItem("Resume indexing", null, (_, _) => _indexingService.Resume())
        {
            Enabled = false,
        };
        menu.Items.Add(_pauseMenuItem);
        menu.Items.Add(_resumeMenuItem);
        menu.Items.Add(new Forms.ToolStripSeparator());
        menu.Items.Add("Exit", null, (_, _) => BeginShutdown());

        _notifyIcon = new Forms.NotifyIcon
        {
            Text = "FileSearch indexer starting",
            Icon = _icon,
            ContextMenuStrip = menu,
            Visible = true,
        };
        _notifyIcon.DoubleClick += (_, _) => ShowFileSearch();

        _startupTask = StartIndexingAsync(_cts.Token);
        _ = RunPipeServerAsync(_cts.Token);
    }

    private static IHost BuildHost() =>
        Host.CreateDefaultBuilder()
            .ConfigureLogging(logging => logging.AddProvider(new FileLoggerProvider(
                Path.Combine(
                    Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                    "FileSearch", "logs"),
                "filesearch-indexer")))
            .ConfigureServices(services =>
            {
                services.AddFileSearchCore();
                services.AddSingleton<WorkerSettingsLoader>();
            })
            .Build();

    private async Task StartIndexingAsync(CancellationToken cancellationToken)
    {
        try
        {
            var settings = _host.Services.GetRequiredService<WorkerSettingsLoader>().Load();
            _indexingService.SetResourceProfile(settings.ResourceProfile);
            _indexingService.SetRuntimeOptions(settings.RuntimeOptions);
            await _indexingService.StartAsync(settings.Locations, cancellationToken).ConfigureAwait(false);
            SetTrayText("FileSearch indexer ready");
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
        }
        catch (Exception ex)
        {
            SetTrayText($"FileSearch indexer error: {ex.Message}");
            _host.Services.GetService<ILoggerFactory>()
                ?.CreateLogger<IndexerApplicationContext>()
                .LogError(ex, "Background indexer startup failed.");
        }
    }

    private async Task RunPipeServerAsync(CancellationToken cancellationToken)
    {
        while (!cancellationToken.IsCancellationRequested)
        {
            try
            {
                await using var pipe = new NamedPipeServerStream(
                    BackgroundIndexerEndpoint.PipeName,
                    PipeDirection.InOut,
                    maxNumberOfServerInstances: 1,
                    PipeTransmissionMode.Byte,
                    PipeOptions.Asynchronous);
                await pipe.WaitForConnectionAsync(cancellationToken).ConfigureAwait(false);

                using var reader = new StreamReader(pipe, Encoding.UTF8, leaveOpen: true);
                await using var writer = new StreamWriter(pipe, Encoding.UTF8, leaveOpen: true)
                {
                    AutoFlush = true,
                };

                var payload = await reader.ReadLineAsync(cancellationToken).ConfigureAwait(false);
                var request = string.IsNullOrWhiteSpace(payload)
                    ? null
                    : JsonSerializer.Deserialize<BackgroundIndexerRequest>(payload, s_jsonOptions);
                var response = await HandleRequestAsync(request, cancellationToken).ConfigureAwait(false);
                var responsePayload = JsonSerializer.Serialize(response, s_jsonOptions);
                await writer.WriteLineAsync(responsePayload).ConfigureAwait(false);
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

    private async Task<BackgroundIndexerResponse> HandleRequestAsync(
        BackgroundIndexerRequest? request,
        CancellationToken cancellationToken)
    {
        if (request is null)
            return new BackgroundIndexerResponse(false, "Invalid request.");

        try
        {
            switch (request.Command)
            {
                case BackgroundIndexerCommand.Ping:
                    return new BackgroundIndexerResponse(true, "FileSearch indexer is running.", _indexingService.CurrentStatus);
                case BackgroundIndexerCommand.GetStatus:
                    return new BackgroundIndexerResponse(true, _indexingService.CurrentStatus.Message, _indexingService.CurrentStatus);
                case BackgroundIndexerCommand.Pause:
                    _indexingService.Pause();
                    return new BackgroundIndexerResponse(true, "Indexing paused.", _indexingService.CurrentStatus);
                case BackgroundIndexerCommand.Resume:
                    _indexingService.Resume();
                    return new BackgroundIndexerResponse(true, "Indexing resumed.", _indexingService.CurrentStatus);
                case BackgroundIndexerCommand.Shutdown:
                    BeginShutdown();
                    return new BackgroundIndexerResponse(true, "FileSearch indexer is shutting down.");
                case BackgroundIndexerCommand.SetResourceProfile:
                    if (request.ResourceProfile is not { } profile)
                        return new BackgroundIndexerResponse(false, "Resource profile is required.");

                    _indexingService.SetResourceProfile(profile);
                    return new BackgroundIndexerResponse(true, $"Indexer resource use set to {profile}.", _indexingService.CurrentStatus);
                case BackgroundIndexerCommand.SetRuntimeOptions:
                    if (request.RuntimeOptions is null)
                        return new BackgroundIndexerResponse(false, "Runtime options are required.");

                    _indexingService.SetRuntimeOptions(request.RuntimeOptions);
                    return new BackgroundIndexerResponse(true, "Indexer runtime limits updated.", _indexingService.CurrentStatus);
                case BackgroundIndexerCommand.SetForegroundSearchActive:
                    if (request.ForegroundSearchActive is not { } foregroundSearchActive)
                        return new BackgroundIndexerResponse(false, "Foreground search state is required.");

                    _indexingService.SetForegroundSearchActive(foregroundSearchActive);
                    return new BackgroundIndexerResponse(true, "Foreground search state updated.", _indexingService.CurrentStatus);
                case BackgroundIndexerCommand.AddOrUpdateLocation:
                    if (request.Location is null)
                        return new BackgroundIndexerResponse(false, "Indexed location is required.");

                    await _startupTask.ConfigureAwait(false);
                    await _indexingService.AddOrUpdateLocationAsync(
                        request.Location.ToIndexedLocation(),
                        queueInitialRefresh: true,
                        cancellationToken).ConfigureAwait(false);
                    return new BackgroundIndexerResponse(true, "Indexed location added.", _indexingService.CurrentStatus);
                case BackgroundIndexerCommand.RefreshRoot:
                    if (request.Location is null)
                        return new BackgroundIndexerResponse(false, "Indexed location is required.");

                    await _startupTask.ConfigureAwait(false);
                    var location = request.Location.ToIndexedLocation();
                    await _indexingService.AddOrUpdateLocationAsync(
                        location,
                        queueInitialRefresh: false,
                        cancellationToken).ConfigureAwait(false);
                    await _indexingService.EnqueueRootRefreshAsync(
                        location.Root,
                        location.WalkerOptions,
                        IndexQueuePriority.High,
                        cancellationToken).ConfigureAwait(false);
                    return new BackgroundIndexerResponse(true, "Index rebuild queued.", _indexingService.CurrentStatus);
                case BackgroundIndexerCommand.QueueRootRefresh:
                    if (request.Location is null)
                        return new BackgroundIndexerResponse(false, "Indexed location is required.");

                    await _startupTask.ConfigureAwait(false);
                    var refreshLocation = request.Location.ToIndexedLocation();
                    await _indexingService.EnqueueRootRefreshAsync(
                        refreshLocation.Root,
                        refreshLocation.WalkerOptions,
                        request.Priority ?? IndexQueuePriority.Low,
                        cancellationToken).ConfigureAwait(false);
                    return new BackgroundIndexerResponse(true, "Index refresh queued.", _indexingService.CurrentStatus);
                case BackgroundIndexerCommand.RemoveLocation:
                    if (string.IsNullOrWhiteSpace(request.Root))
                        return new BackgroundIndexerResponse(false, "Indexed root is required.");

                    await _startupTask.ConfigureAwait(false);
                    await _indexingService.RemoveLocationAsync(request.Root, cancellationToken).ConfigureAwait(false);
                    return new BackgroundIndexerResponse(true, "Indexed location removed.", _indexingService.CurrentStatus);
                case BackgroundIndexerCommand.CompactDatabase:
                    return await CompactDatabaseAsync(cancellationToken).ConfigureAwait(false);
                case BackgroundIndexerCommand.ValidateRoot:
                    if (request.Location is null)
                        return new BackgroundIndexerResponse(false, "Indexed location is required.");

                    return await ValidateRootAsync(request.Location.ToIndexedLocation(), cancellationToken)
                        .ConfigureAwait(false);
                default:
                    return new BackgroundIndexerResponse(false, $"Unsupported command: {request.Command}.");
            }
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            return new BackgroundIndexerResponse(false, ex.Message, _indexingService.CurrentStatus);
        }
    }

    private async Task<BackgroundIndexerResponse> CompactDatabaseAsync(CancellationToken cancellationToken)
    {
        await _startupTask.ConfigureAwait(false);

        var resumeIndexing = !_indexingService.IsPaused;
        if (resumeIndexing)
            _indexingService.Pause();

        try
        {
            if (!await WaitForIdleAsync(TimeSpan.FromSeconds(30), cancellationToken).ConfigureAwait(false))
                return new BackgroundIndexerResponse(false, "Indexer is still busy; compact later.", _indexingService.CurrentStatus);

            await _fileIndex.CompactAsync(cancellationToken).ConfigureAwait(false);
            return new BackgroundIndexerResponse(true, "Index database compacted.", _indexingService.CurrentStatus);
        }
        finally
        {
            if (resumeIndexing)
                _indexingService.Resume();
        }
    }

    private async Task<BackgroundIndexerResponse> ValidateRootAsync(
        IndexedLocation location,
        CancellationToken cancellationToken)
    {
        await _startupTask.ConfigureAwait(false);

        if (!await WaitForIdleAsync(TimeSpan.FromSeconds(30), cancellationToken).ConfigureAwait(false))
            return new BackgroundIndexerResponse(false, "Indexer is still busy; validate later.", _indexingService.CurrentStatus);

        var validation = await _fileIndex.ValidateRootAsync(
                new IndexRequest(location.Root, location.WalkerOptions),
                cancellationToken)
            .ConfigureAwait(false);
        return new BackgroundIndexerResponse(
            true,
            validation.Message,
            _indexingService.CurrentStatus,
            validation);
    }

    private async Task<bool> WaitForIdleAsync(TimeSpan timeout, CancellationToken cancellationToken)
    {
        var deadline = DateTime.UtcNow.Add(timeout);
        while (IsIndexerBusy() && DateTime.UtcNow < deadline)
            await Task.Delay(200, cancellationToken).ConfigureAwait(false);

        return !IsIndexerBusy();
    }

    private bool IsIndexerBusy() =>
        _indexingService.CurrentStatus is { IsProcessing: true } ||
        _indexingService.CurrentStatus.QueueLength > 0;

    private void OnIndexingStatusChanged(object? sender, IndexingStatus status)
    {
        _uiContext.Post(_ =>
        {
            _pauseMenuItem.Enabled = !status.IsPaused;
            _resumeMenuItem.Enabled = status.IsPaused;
            SetTrayText(status.Message);
        }, null);
    }

    private void ShowFileSearch()
    {
        var guiPath = ResolveGuiExecutablePath();
        if (guiPath is null)
            return;

        Process.Start(new ProcessStartInfo(guiPath)
        {
            UseShellExecute = true,
        });
        BeginShutdown();
    }

    private static string? ResolveGuiExecutablePath()
    {
        var baseDirectory = AppContext.BaseDirectory;
        var sameDirectory = Path.Combine(baseDirectory, "FileSearch.Gui.exe");
        if (File.Exists(sameDirectory))
            return sameDirectory;

        var devPath = Path.GetFullPath(Path.Combine(
            baseDirectory,
            "..", "..", "..", "..",
            "FileSearch.Gui",
            "bin",
            "Debug",
            "net10.0-windows",
            "FileSearch.Gui.exe"));
        return File.Exists(devPath) ? devPath : null;
    }

    private void BeginShutdown()
    {
        if (Interlocked.Exchange(ref _stopping, 1) != 0)
            return;

        _ = ShutdownAsync();
    }

    private async Task ShutdownAsync()
    {
        try
        {
            _cts.Cancel();
            await _indexingService.StopAsync(CancellationToken.None).WaitAsync(TimeSpan.FromSeconds(5)).ConfigureAwait(false);
        }
        catch
        {
        }
        finally
        {
            _uiContext.Post(_ => ExitThread(), null);
        }
    }

    private void SetTrayText(string text)
    {
        var normalized = string.IsNullOrWhiteSpace(text) ? "FileSearch indexer" : text.Trim();
        _notifyIcon.Text = normalized.Length <= 63 ? normalized : normalized[..60] + "...";
    }

    private static Icon LoadTrayIconImage()
    {
        if (!string.IsNullOrWhiteSpace(Environment.ProcessPath))
        {
            using var associatedIcon = Icon.ExtractAssociatedIcon(Environment.ProcessPath);
            if (associatedIcon is not null)
                return (Icon)associatedIcon.Clone();
        }

        return (Icon)SystemIcons.Application.Clone();
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            _indexingService.StatusChanged -= OnIndexingStatusChanged;
            _notifyIcon.Dispose();
            _icon.Dispose();
            _cts.Dispose();
            _host.Dispose();
        }

        base.Dispose(disposing);
    }
}
