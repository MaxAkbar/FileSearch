using System.Collections.Concurrent;
using System.ComponentModel;
using System.Diagnostics;
using System.Reflection;
using System.Security.Cryptography;
using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Threading;
using FileSearch.Core.Databases;
using FileSearch.Gui.Controls;

// Probe-only observation: no deadlines, priorities, or application state are changed.
internal sealed class ExpandedDatabaseLoadTrace : IDisposable
{
    private const int MaximumEvents = 2048;
    private static readonly string[] ExpectedPhases = ["page.ui-setup", "page.query", "page.mapping", "page.ui-resume-wait",
        "page.columns", "page.items-source", "page.dispatch-wait", "page.layout-before", "page.selection-scroll",
        "page.layout-after", "page.horizontal-scroll", "page.total", "keep.scheduled", "keep.dispatch-wait",
        "keep.layout-before", "keep.scroll", "keep.layout-after", "keep.horizontal-scroll", "keep.total"];
    private readonly string _output;
    private readonly FileDocumentPreview _view;
    private readonly ContentControl _body;
    private readonly DependencyPropertyDescriptor _bodyDescriptor;
    private readonly Stopwatch _clock = Stopwatch.StartNew();
    private readonly ConcurrentQueue<TraceEvent> _events = new();
    private readonly ConcurrentDictionary<string, byte> _observedPhases = new(StringComparer.Ordinal);
    private readonly List<ControlWatch> _controls = [];
    private readonly DispatcherTimer _heartbeat;
    private readonly SourceState _sourceBefore;
    private double _lastHeartbeat;
    private int _droppedEvents;
    private bool _disposed, _completed;

    private sealed record TraceEvent(double ElapsedMilliseconds, DateTime Utc, int ManagedThreadId,
        string Kind, string Name, double? DurationMilliseconds, object? State);
    private sealed record SourceState(string? Sha256, long? Bytes, DateTime? LastWriteUtc, string? Error);
    private sealed class ControlWatch(DatabasePreviewControl control)
    {
        internal DatabasePreviewControl Control { get; } = control;
        internal PropertyInfo? Hook;
        internal Action<string, double>? Previous, Callback;
        internal TextBlock? Status;
        internal DependencyPropertyDescriptor? StatusDescriptor;
        internal EventHandler? StatusHandler;
    }

    internal ExpandedDatabaseLoadTrace(FileDocumentPreview view, string output)
    {
        _view = view;
        _output = output;
        _sourceBefore = ReadSource(view.Result?.FullPath);
        _body = view.FindName("Body") as ContentControl ?? throw new InvalidOperationException("The shared preview has no Body control to trace.");
        _bodyDescriptor = DependencyPropertyDescriptor.FromProperty(ContentControl.ContentProperty, typeof(ContentControl))
            ?? throw new InvalidOperationException("The shared preview Body content cannot be observed.");
        _bodyDescriptor.AddValueChanged(_body, OnBodyChanged);
        Record("observer", "attached-before-settle", state: ViewState());
        AttachCurrentBody();
        _heartbeat = new DispatcherTimer(DispatcherPriority.Input, view.Dispatcher) { Interval = TimeSpan.FromMilliseconds(100) };
        _heartbeat.Tick += OnHeartbeat;
        _heartbeat.Start();
    }

    private void OnBodyChanged(object? sender, EventArgs args)
    {
        Record("body", _body.Content?.GetType().FullName ?? "empty", state: ViewState());
        AttachCurrentBody();
    }

    private void AttachCurrentBody()
    {
        if (_body.Content is not DatabasePreviewControl database || _controls.Any(watch => ReferenceEquals(watch.Control, database))) return;
        if (_controls.Count >= 8) { Record("observer", "control-watch-limit"); return; }
        var watch = new ControlWatch(database)
        {
            Hook = typeof(DatabasePreviewControl).GetProperty("PagePhaseMeasured", BindingFlags.Instance | BindingFlags.NonPublic),
            Status = Field(database, "_status") as TextBlock,
        };
        _controls.Add(watch);
        if (watch.Hook is { } hook)
        {
            watch.Previous = hook.GetValue(database) as Action<string, double>;
            watch.Callback = (name, milliseconds) =>
            {
                _observedPhases.TryAdd(name, 0);
                Record("phase", name, milliseconds);
                watch.Previous?.Invoke(name, milliseconds);
            };
            hook.SetValue(database, watch.Callback);
        }
        if (watch.Status is { } status)
        {
            watch.StatusDescriptor = DependencyPropertyDescriptor.FromProperty(TextBlock.TextProperty, typeof(TextBlock));
            watch.StatusHandler = (_, _) => Record("database-status", status.Text, state: DatabaseState(database));
            watch.StatusDescriptor?.AddValueChanged(status, watch.StatusHandler);
        }
        Record("database", watch.Hook is null ? "attached-without-phase-hook" : "attached-with-phase-hook", state: DatabaseState(database));
    }

    private void OnHeartbeat(object? sender, EventArgs args)
    {
        double elapsed = _clock.Elapsed.TotalMilliseconds;
        double gap = elapsed - _lastHeartbeat;
        _lastHeartbeat = elapsed;
        Record("dispatcher-heartbeat", "input-priority", gap, new
        {
            view = ViewState(),
            databases = _controls.Select(watch => DatabaseState(watch.Control)).ToArray(),
        });
    }

    private object ViewState() => new
    {
        _view.PreviewState, _view.IsLoaded, _view.IsVisible,
        bodyType = _body.Content?.GetType().FullName,
        load = Cancellation(Field(_view, "_load")),
        pendingDatabaseLocation = Field(_view, "_expandedDatabaseLocation") is not null,
        pendingFindRestore = Field(_view, "_expandedDatabaseFindState") is not null,
        summary = (_view.FindName("Summary") as TextBlock)?.Text,
        status = (_view.FindName("Status") as TextBlock)?.Text,
    };

    private static object DatabaseState(DatabasePreviewControl database)
    {
        var session = Field(database, "_session") as DatabaseReadSession;
        var grid = Field(database, "_grid") as DataGrid;
        return new
        {
            database.PreviewState, database.FindState, database.FindStatusText, database.CurrentLocation,
            database.IsLoaded, database.IsVisible, disposed = Field(database, "_disposed"),
            sourceGeneration = Field(database, "_sourceGeneration"), pageGeneration = Field(database, "_generation"),
            status = (Field(database, "_status") as TextBlock)?.Text,
            opening = Cancellation(Field(database, "_openingLoad")), page = Cancellation(Field(database, "_pageLoad")),
            lifetime = Cancellation(Field(database, "_lifetime")),
            hasSession = session is not null,
            snapshotPath = session is null ? null : typeof(DatabaseReadSession).GetProperty("SnapshotPath", BindingFlags.Instance | BindingFlags.NonPublic)?.GetValue(session),
            grid = grid is null ? null : new { grid.IsLoaded, grid.IsVisible, grid.ActualWidth, grid.ActualHeight, rows = grid.Items.Count },
        };
    }

    private static object Cancellation(object? value)
    {
        if (value is not CancellationTokenSource source) return new { present = false, canceled = (bool?)null };
        try { return new { present = true, canceled = (bool?)source.IsCancellationRequested }; }
        catch (ObjectDisposedException) { return new { present = true, disposed = true }; }
    }

    private void Record(string kind, string name, double? duration = null, object? state = null)
    {
        _events.Enqueue(new(_clock.Elapsed.TotalMilliseconds, DateTime.UtcNow, Environment.CurrentManagedThreadId, kind, name, duration, state));
        while (_events.Count > MaximumEvents)
            if (_events.TryDequeue(out _)) Interlocked.Increment(ref _droppedEvents);
    }

    internal void Complete(string outcome, Exception? failure = null)
    {
        if (_completed) return;
        _completed = true;
        try
        {
            OnHeartbeat(null, EventArgs.Empty);
            Record("observer", outcome, state: new { error = failure?.ToString() });
            var after = ReadSource(_view.Result?.FullPath);
            var observed = _observedPhases.Keys.Order(StringComparer.Ordinal).ToArray();
            File.WriteAllText(_output, JsonSerializer.Serialize(new
            {
                outcome, failure = failure?.ToString(), elapsedMilliseconds = _clock.Elapsed.TotalMilliseconds,
                path = _view.Result?.FullPath, sourceBefore = _sourceBefore, sourceAfter = after,
                sourceBytesUnchanged = _sourceBefore.Sha256 is null || after.Sha256 is null ? (bool?)null : _sourceBefore.Sha256 == after.Sha256,
                observedPhases = observed, absentPhases = ExpectedPhases.Except(observed).ToArray(),
                droppedEvents = _droppedEvents, events = _events.OrderBy(item => item.ElapsedMilliseconds).ToArray(),
                interpretation = "Probe-only status/phase observation; Input-priority heartbeat gaps include dispatcher and scheduling delay. " +
                    "Database preparation status is delivered on the dispatcher. Page/keep clocks overlap; do not sum them. " +
                    "Absent phases were not observed and are not zero-duration work. Hooks and snapshots add diagnostic overhead; compare instrumented runs separately.",
            }, new JsonSerializerOptions { WriteIndented = true, NumberHandling = System.Text.Json.Serialization.JsonNumberHandling.AllowNamedFloatingPointLiterals }));
        }
        catch (Exception error)
        {
            try { File.WriteAllText(_output + ".diagnostic-error.txt", error.ToString()); }
            catch (Exception) { /* A diagnostic failure must not replace the probe's original outcome. */ }
        }
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        _heartbeat.Stop();
        _heartbeat.Tick -= OnHeartbeat;
        _bodyDescriptor.RemoveValueChanged(_body, OnBodyChanged);
        if (!_completed) Complete("scope-ended-before-ready");
        foreach (var watch in _controls)
        {
            if (watch.Status is not null && watch.StatusHandler is not null)
                watch.StatusDescriptor?.RemoveValueChanged(watch.Status, watch.StatusHandler);
            if (watch.Hook is { } hook && ReferenceEquals(hook.GetValue(watch.Control), watch.Callback))
                hook.SetValue(watch.Control, watch.Previous);
        }
    }

    private static object? Field(object owner, string name) => owner.GetType().GetField(name, BindingFlags.Instance | BindingFlags.NonPublic)?.GetValue(owner);
    private static SourceState ReadSource(string? path)
    {
        try
        {
            using var file = new FileStream(path!, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
            var info = new FileInfo(path!);
            return new(Convert.ToHexString(SHA256.HashData(file)), info.Length, info.LastWriteTimeUtc, null);
        }
        catch (Exception error) { return new(null, null, null, error.ToString()); }
    }
}
