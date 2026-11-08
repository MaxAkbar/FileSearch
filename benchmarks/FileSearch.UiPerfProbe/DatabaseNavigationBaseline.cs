using System.Collections.Concurrent;
using System.Data;
using System.Diagnostics;
using System.Globalization;
using System.Reflection;
using System.Security.Cryptography;
using System.Text.Json;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Threading;
using FileSearch.Core.Databases;
using FileSearch.Gui.Controls;

// A diagnostic of the real shared database viewer, not a complete user task.
internal static class DatabaseNavigationBaseline
{
    private const string Table = "Customer \"records\"";
    private const string Marker = "renewal 東京 O'Brien";
    private const int PageSize = 250;
    private static readonly string[] Sidecars = ["", "-wal", ".wal", "-shm", ".shm", "-journal"];
    private sealed record SourcePart(string Path, bool Exists, long Bytes, string? Sha256);
    private sealed record Source(DatabaseKind Engine, string Path, string Fingerprint, IReadOnlyList<SourcePart> Parts);
    private sealed record Phase(string Name, double Milliseconds, int ManagedThreadId);
    private sealed record GridCounters(int RealizedRows, int RealizedCells, double Width, double Height,
        bool IsVirtualizing, string VirtualizationMode, string ScrollUnit, bool? CanContentScroll,
        double? ExtentHeight, double? ViewportHeight, double? VerticalOffset);
    private sealed record Sample(DatabaseKind Engine, int Iteration, int Position, string Operation, long ExpectedRow,
        bool Success, double? ReadyMs, double? HeartbeatP95Ms, double? HeartbeatMaxMs, int HeartbeatSamples,
        long AllocatedBytes, long WorkingSetBeforeBytes, long WorkingSetAfterBytes, long WorkingSetPeakBytes,
        string State, DatabaseLocation? Location, string? Id, string? Notes, long? FirstPageRow, int GridRows,
        string? Error, IReadOnlyList<Phase>? Phases, GridCounters? Grid);

    public static async Task RunAsync(string output, string manifestPath, int repeats, bool collectPhases = false)
    {
        if (repeats is < 3 or > 1000) throw new ArgumentOutOfRangeException(nameof(repeats));
        output = Path.GetFullPath(output);
        Directory.CreateDirectory(Path.GetDirectoryName(output)!);
        using var manifest = JsonDocument.Parse(File.ReadAllText(manifestPath));
        var document = manifest.RootElement;
        if (document.GetProperty("Version").GetInt32() != 1) throw new InvalidDataException("Unsupported task manifest.");
        int rows = document.GetProperty("DatabaseRows").GetInt32();
        if (rows is <= 750 or > DatabaseFiles.MaxRows) throw new InvalidDataException("Use a generated corpus containing 751 to 100,000 database rows.");
        string root = Path.GetFullPath(document.GetProperty("ContentRoot").GetString()!);
        var sources = new[] { (DatabaseKind.Sqlite, "records.sqlite"), (DatabaseKind.CSharpDb, "records.csdb") }
            .Select(item =>
            {
                string path = Path.Combine(root, item.Item2);
                string expectedHash = document.GetProperty("Files").EnumerateArray()
                    .Single(file => file.GetProperty("Path").GetString() == item.Item2).GetProperty("Sha256").GetString()!;
                if (Hash(path) != expectedHash) throw new InvalidDataException("Corpus changed: " + path);
                return CaptureSource(item.Item1, path);
            }).ToArray();
        var started = DateTime.UtcNow;
        var samples = new List<Sample>();
        bool passed = true;
        await using var checkpoint = new StreamWriter(Path.ChangeExtension(output, ".samples.jsonl")) { AutoFlush = true };
        for (int iteration = 0; iteration <= repeats; iteration++)
        {
            var ordered = iteration % 2 == 0 ? sources : sources.Reverse().ToArray();
            for (int position = 0; position < ordered.Length; position++)
            {
                var source = ordered[position];
                var database = new DatabasePreviewControl();
                var window = new Window
                {
                    Content = database, Width = 1000, Height = 760, ShowActivated = false, ShowInTaskbar = false,
                    Left = -32000, Top = -32000, WindowStartupLocation = WindowStartupLocation.Manual,
                };
                window.Show();
                await SettleAsync(window);
                try
                {
                    var schedule = new (string Name, long Row, Func<Task> Start)[]
                    {
                        ("open", 1, () => database.InitializeAsync(source.Path, null, CancellationToken.None)),
                        ("next-1", 251, () => ClickAsync(database, "Rows ›")),
                        ("next-2", 501, () => ClickAsync(database, "Rows ›")),
                        ("go-deep", rows, () => GoAsync(database, rows)),
                        ("previous-deep", Math.Max(1, ((rows - 1) / PageSize - 1) * PageSize + 1), () => ClickAsync(database, "‹ Rows")),
                        ("go-first", 1, () => GoAsync(database, 1)),
                        ("same-source-750", 750, () => database.InitializeAsync(source.Path,
                            new DatabaseLocation(source.Engine, Table, 750, "Notes",
                                source.Engine == DatabaseKind.Sqlite ? "rowid=750" : "Id=750", source.Fingerprint), CancellationToken.None)),
                    };
                    foreach (var operation in schedule)
                    {
                        var sample = await MeasureAsync(window, database, source, iteration, position + 1,
                            operation.Name, operation.Row, rows, operation.Start, collectPhases);
                        samples.Add(sample);
                        passed &= sample.Success;
                        await checkpoint.WriteLineAsync(JsonSerializer.Serialize(sample));
                    }
                }
                finally
                {
                    // Disposal is outside samples, and finishes before the next measured session.
                    var session = typeof(DatabasePreviewControl).GetField("_session", BindingFlags.NonPublic | BindingFlags.Instance)?.GetValue(database) as DatabaseReadSession;
                    database.Dispose();
                    if (session is not null) await session.DisposeAsync();
                    window.Close();
                    await Dispatcher.Yield(DispatcherPriority.ApplicationIdle);
                }
            }
        }
        bool unchanged = sources.All(before =>
        {
            var after = CaptureSource(before.Engine, before.Path);
            return before.Fingerprint == after.Fingerprint && before.Parts.SequenceEqual(after.Parts);
        });
        passed &= unchanged;
        var summaries = samples.Where(sample => sample.Iteration > 0).GroupBy(sample => (sample.Engine, sample.Operation)).Select(group => new
        {
            group.Key.Engine, group.Key.Operation, samples = group.Count(), successes = group.Count(sample => sample.Success),
            readyP50Ms = Percentile(group.Select(sample => sample.ReadyMs), .50),
            readyP95Ms = Percentile(group.Select(sample => sample.ReadyMs), .95),
            heartbeatMaxMs = group.Max(sample => sample.HeartbeatMaxMs),
            allocatedP50Bytes = Percentile(group.Select(sample => (double?)sample.AllocatedBytes), .50),
            workingSetPeakBytes = group.Max(sample => sample.WorkingSetPeakBytes),
        }).ToArray();
        var phaseSummaries = samples.Where(sample => sample.Iteration > 0 && sample.Phases is not null)
            .SelectMany(sample => sample.Phases!.GroupBy(phase => phase.Name).Select(group => new
            {
                sample.Engine, sample.Operation, Name = group.Key, Count = group.Count(),
                Milliseconds = group.Sum(phase => phase.Milliseconds),
            }))
            .GroupBy(value => (value.Engine, value.Operation, value.Name)).Select(group => new
            {
                group.Key.Engine, group.Key.Operation, phase = group.Key.Name, samples = group.Count(),
                events = group.Sum(value => value.Count),
                summedPerOperationP50Ms = Percentile(group.Select(value => (double?)value.Milliseconds), .50),
                summedPerOperationP95Ms = Percentile(group.Select(value => (double?)value.Milliseconds), .95),
            }).ToArray();
        File.WriteAllText(output, JsonSerializer.Serialize(new
        {
            startedUtc = started, finishedUtc = DateTime.UtcNow, manifestPath = Path.GetFullPath(manifestPath), rows, repeats,
            os = Environment.OSVersion.ToString(), runtime = Environment.Version.ToString(), processorCount = Environment.ProcessorCount,
            assembly = typeof(DatabasePreviewControl).Assembly.Location,
            assemblySha256 = Hash(typeof(DatabasePreviewControl).Assembly.Location),
            coreAssembly = typeof(DatabaseReadSession).Assembly.Location,
            coreAssemblySha256 = Hash(typeof(DatabaseReadSession).Assembly.Location),
            control = "Real shared DatabasePreviewControl in an offscreen window. Actual Next/Previous/Go buttons; no fake database. Not a main/Quick user task, physical-input, presentation-time, or performance qualification.",
            method = "Fresh control/session per engine/round. Alternating engine order. Iteration zero is first invocation, not OS-cold, retained and excluded from warm summaries. 10 ms readiness polling plus application-idle layout. Readiness includes snapshot/schema/query/grid as applicable, not separately timed components. 16 ms Input-priority heartbeat gaps proxy dispatcher responsiveness; final partial intervals retained. Process-total allocations are churn, working-set samples are not retained-memory measurements. Working-set peak is sampled at heartbeat/poll boundaries. No cache flush or settings changes. Small repeat counts give coarse tail estimates. Failures retained and fail the probe.",
            collectPhases,
            phaseMethod = collectPhases ? "Per-control opt-in clocks; normal previews do not collect these phases. Query/mapping use background threads; UI-resume and ContextIdle waits include other dispatcher work. Keep-cell-visible phases can overlap page phases and totals; do not add nested phase totals. Scheduled events have zero duration and are counted. Phase callback/queue and realized-row/cell counters add diagnostic overhead; compare instrumented before/after separately from ordinary readiness. Counters are captured after readiness timing. Missing phase events remain absent rather than becoming zero work." : null,
            sources, sourcesUnchanged = unchanged, passed, summaries, phaseSummaries, samples,
        }, new JsonSerializerOptions { WriteIndented = true }));
        if (!passed) throw new InvalidOperationException("Database navigation baseline recorded failures; inspect JSON and samples.");
    }

    private static async Task<Sample> MeasureAsync(Window window, DatabasePreviewControl database, Source source,
        int iteration, int position, string operation, long expectedRow, int totalRows, Func<Task> action, bool collectPhases)
    {
        var phases = collectPhases ? new ConcurrentQueue<Phase>() : null;
        var hook = collectPhases ? typeof(DatabasePreviewControl).GetProperty("PagePhaseMeasured", BindingFlags.NonPublic | BindingFlags.Instance)
            ?? throw new InvalidOperationException("The shared database preview has no opt-in phase hook.") : null;
        var previousHook = hook?.GetValue(database);
        if (hook is not null) hook.SetValue(database, new Action<string, double>((name, milliseconds) =>
            phases!.Enqueue(new(name, milliseconds, Environment.CurrentManagedThreadId))));
        var gaps = new List<double>();
        long lastTick = Stopwatch.GetTimestamp();
        long workingBefore = Environment.WorkingSet, workingPeak = workingBefore;
        var heartbeat = new DispatcherTimer(DispatcherPriority.Input) { Interval = TimeSpan.FromMilliseconds(16) };
        heartbeat.Tick += (_, _) =>
        {
            long now = Stopwatch.GetTimestamp();
            gaps.Add(Stopwatch.GetElapsedTime(lastTick, now).TotalMilliseconds);
            lastTick = now;
            workingPeak = Math.Max(workingPeak, Environment.WorkingSet);
        };
        long allocated = GC.GetTotalAllocatedBytes(false);
        var clock = Stopwatch.StartNew();
        bool success = false;
        string? error = null, id = null, notes = null;
        long? first = null;
        int gridRows = 0;
        heartbeat.Start();
        try
        {
            Task operationTask = action();
            while (!operationTask.IsCompleted || database.PreviewState == "Loading")
            {
                if (clock.Elapsed > TimeSpan.FromSeconds(30)) throw new TimeoutException("Database operation did not settle.");
                await Task.Delay(10);
                workingPeak = Math.Max(workingPeak, Environment.WorkingSet);
            }
            await operationTask;
            await SettleAsync(window);
            var grid = Descendants(database).OfType<DataGrid>().Single(control => control.Name == "DatabaseRecordsGrid");
            gridRows = grid.Items.Count;
            first = grid.Items.Count > 0 && grid.Items[0] is DataRowView firstRecord ? (long)firstRecord["r"] : null;
            if (grid.CurrentCell.Item is DataRowView selected)
            {
                id = selected["c0"].ToString();
                notes = selected["c1"].ToString();
            }
            var location = database.CurrentLocation;
            string expectedKey = (source.Engine == DatabaseKind.Sqlite ? "rowid=" : "Id=") + expectedRow.ToString(CultureInfo.InvariantCulture);
            long expectedFirst = ((expectedRow - 1) / PageSize) * PageSize + 1;
            success = database.PreviewState == "Ready" && location is not null && location.Engine == source.Engine &&
                location.Table == Table && location.RowNumber == expectedRow && location.RowKey == expectedKey &&
                location.Fingerprint == source.Fingerprint && first == expectedFirst &&
                gridRows == Math.Min(PageSize, totalRows - expectedFirst + 1) &&
                id == expectedRow.ToString(CultureInfo.InvariantCulture) && notes == (expectedRow == totalRows ? Marker : "Ordinary record");
            if (!success) error = "Unexpected state, selected record/value/key/fingerprint, or page range.";
        }
        catch (Exception ex) { error = ex.GetType().Name + ": " + ex.Message; }
        finally { heartbeat.Stop(); hook?.SetValue(database, previousHook); }
        double elapsed = clock.Elapsed.TotalMilliseconds;
        gaps.Add(Stopwatch.GetElapsedTime(lastTick).TotalMilliseconds);
        long workingAfter = Environment.WorkingSet;
        workingPeak = Math.Max(workingPeak, workingAfter);
        long allocatedBytes = GC.GetTotalAllocatedBytes(false) - allocated;
        GridCounters? counters = null;
        if (collectPhases)
        {
            var grid = Descendants(database).OfType<DataGrid>().Single(control => control.Name == "DatabaseRecordsGrid");
            var descendants = Descendants(grid).ToArray();
            var scroll = descendants.OfType<ScrollViewer>().FirstOrDefault();
            counters = new(descendants.OfType<DataGridRow>().Count(), descendants.OfType<DataGridCell>().Count(),
                grid.ActualWidth, grid.ActualHeight, VirtualizingPanel.GetIsVirtualizing(grid),
                VirtualizingPanel.GetVirtualizationMode(grid).ToString(), VirtualizingPanel.GetScrollUnit(grid).ToString(),
                scroll?.CanContentScroll, scroll?.ExtentHeight, scroll?.ViewportHeight, scroll?.VerticalOffset);
        }
        return new(source.Engine, iteration, position, operation, expectedRow, success, elapsed,
            Percentile(gaps.Select(value => (double?)value), .95), gaps.Max(), gaps.Count,
            allocatedBytes, workingBefore, workingAfter, workingPeak,
            database.PreviewState, database.CurrentLocation, id, notes, first, gridRows, error, phases?.ToArray(), counters);
    }

    private static Task GoAsync(DatabasePreviewControl database, long row)
    {
        Descendants(database).OfType<TextBox>().Single(control => AutomationProperties.GetName(control) == "Database row number").Text = row.ToString(CultureInfo.InvariantCulture);
        return ClickAsync(database, "Go to row");
    }

    private static Task ClickAsync(DatabasePreviewControl database, string content)
    {
        var button = Descendants(database).OfType<Button>().Single(control => control.Content?.ToString() == content);
        if (!button.IsEnabled) throw new InvalidOperationException("Navigation button is disabled: " + content);
        button.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
        return Task.CompletedTask;
    }

    private static async Task SettleAsync(Window window)
    {
        window.UpdateLayout();
        await Dispatcher.Yield(DispatcherPriority.ApplicationIdle);
        window.UpdateLayout();
        await Dispatcher.Yield(DispatcherPriority.ApplicationIdle);
    }

    private static double? Percentile(IEnumerable<double?> values, double p)
    {
        var array = values.Where(value => value.HasValue).Select(value => value!.Value).Order().ToArray();
        return array.Length == 0 ? null : array[Math.Clamp((int)Math.Ceiling(array.Length * p) - 1, 0, array.Length - 1)];
    }

    private static Source CaptureSource(DatabaseKind engine, string path) => new(engine, path, DatabaseFiles.Fingerprint(path),
        Sidecars.Select(suffix =>
        {
            string member = path + suffix;
            var info = new FileInfo(member);
            return new SourcePart(member, info.Exists, info.Exists ? info.Length : 0, info.Exists ? Hash(member) : null);
        }).ToArray());

    private static string Hash(string path)
    {
        using var stream = File.OpenRead(path);
        return Convert.ToHexString(SHA256.HashData(stream));
    }

    private static IEnumerable<DependencyObject> Descendants(DependencyObject parent)
    {
        for (int index = 0; index < VisualTreeHelper.GetChildrenCount(parent); index++)
        {
            var child = VisualTreeHelper.GetChild(parent, index);
            yield return child;
            foreach (var descendant in Descendants(child)) yield return descendant;
        }
    }
}
