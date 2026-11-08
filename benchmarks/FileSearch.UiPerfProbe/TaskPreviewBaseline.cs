using System.Diagnostics;
using System.Globalization;
using System.Security.Cryptography;
using System.Text.Json;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using FileSearch.Core.Databases;
using FileSearch.Core.Engine;
using FileSearch.Core.Extractors;
using FileSearch.Core.Indexing;
using FileSearch.Core.Queries;
using FileSearch.Core.Walker;
using FileSearch.Gui.Controls;
using FileSearch.Gui.Tests;
using FileSearch.Gui.ViewModels;
using FileSearch.WindowsOcr;
using Windows.Media.Ocr;

internal static class TaskPreviewBaseline
{
    public static async Task RunAsync(string output, string manifestPath, int repeats, bool includeOcr)
    {
        if (repeats is < 3 or > 1000) throw new ArgumentOutOfRangeException(nameof(repeats));
        using var manifest = JsonDocument.Parse(File.ReadAllText(manifestPath));
        string root = manifest.RootElement.GetProperty("ContentRoot").GetString()!;
        var paths = manifest.RootElement.GetProperty("Files").EnumerateArray().Select(file =>
            (Path: Path.Combine(root, file.GetProperty("Path").GetString()!), Hash: file.GetProperty("Sha256").GetString()!)).ToArray();
        foreach (var file in paths)
            if (Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(file.Path))) != file.Hash) throw new InvalidOperationException("Corpus changed: " + file.Path);

        string directory = Path.GetDirectoryName(Path.GetFullPath(output))!;
        Directory.CreateDirectory(directory);
        string scanPath = Path.Combine(directory, $"scan-{Guid.NewGuid():N}.png");
        var visual = new DrawingVisual();
        using (var drawing = visual.RenderOpen())
        {
            drawing.DrawRectangle(Brushes.White, null, new Rect(0, 0, 1000, 500));
            drawing.DrawText(new FormattedText("Annual renewal approved", CultureInfo.InvariantCulture,
                FlowDirection.LeftToRight, new Typeface("Segoe UI"), 42, Brushes.Black, 1), new Point(60, 180));
        }
        var image = new RenderTargetBitmap(1000, 500, 96, 96, PixelFormats.Pbgra32);
        image.Render(visual);
        var encoder = new PngBitmapEncoder(); encoder.Frames.Add(BitmapFrame.Create(image));
        using (var stream = File.Create(scanPath)) encoder.Save(stream);

        var preview = new FileDocumentPreview();
        var window = new Window { Content = preview, Width = 1000, Height = 760, ShowActivated = false, ShowInTaskbar = false, Left = -32000, Top = -32000, WindowStartupLocation = WindowStartupLocation.Manual };
        var rows = new List<object>();
        bool passed = true;
        window.Show();
        try
        {
            var files = new[] { "renewal-agreement.txt", "source.cs", "policy.pdf", "budget.xlsx", "message.eml", "records.sqlite", "records.csdb" }
                .Select(file => Path.Combine(root, file)).Append(scanPath);
            foreach (string path in files)
            {
                for (int iteration = 0; iteration <= repeats; iteration++)
                {
                    preview.Result = null;
                    await Dispatcher.Yield(DispatcherPriority.ApplicationIdle);
                    var gaps = new List<double>();
                    long previous = Stopwatch.GetTimestamp();
                    var heartbeat = new DispatcherTimer(DispatcherPriority.Input) { Interval = TimeSpan.FromMilliseconds(16) };
                    heartbeat.Tick += (_, _) => { long now = Stopwatch.GetTimestamp(); gaps.Add(Stopwatch.GetElapsedTime(previous, now).TotalMilliseconds); previous = now; };
                    var clock = Stopwatch.StartNew();
                    long allocation = GC.GetTotalAllocatedBytes(false);
                    heartbeat.Start();
                    string? error = null;
                    bool success = false;
                    try
                    {
                        preview.Result = new FileResultViewModel(path, new FakeFileLauncher());
                        do
                        {
                            await Task.Delay(10);
                            window.UpdateLayout();
                            await Dispatcher.Yield(DispatcherPriority.ApplicationIdle);
                            if (clock.Elapsed > TimeSpan.FromSeconds(30)) throw new TimeoutException("Preview did not settle.");
                        } while (preview.PreviewState is "Empty" or "Loading");
                        string expected = Path.GetExtension(path) switch
                        {
                            ".pdf" => "Pdf",
                            ".xlsx" => "Spreadsheet",
                            ".eml" => "Html",
                            ".sqlite" or ".csdb" => "Database",
                            ".png" => "Image",
                            _ => "Text"
                        };
                        success = preview.PreviewState == expected;
                        if (expected == "Database")
                            success &= Descendants(preview).OfType<DatabasePreviewControl>().Single().PreviewState == "Ready";
                    }
                    catch (Exception ex) { error = ex.GetType().Name + ": " + ex.Message; }
                    finally { heartbeat.Stop(); }
                    double elapsed = clock.Elapsed.TotalMilliseconds;
                    gaps.Add(Stopwatch.GetElapsedTime(previous).TotalMilliseconds); // includes a final stall even for sub-tick loads
                    passed &= success;
                    rows.Add(new
                    {
                        file = Path.GetFileName(path),
                        iteration,
                        condition = iteration == 0 ? "first invocation, not OS-cold" : "warm",
                        success,
                        state = preview.PreviewState,
                        firstPageSettledMs = elapsed,
                        heartbeatSamples = gaps.Count,
                        heartbeatP95Ms = Percentile(gaps, .95),
                        heartbeatMaxMs = gaps.Max(),
                        allocatedBytes = GC.GetTotalAllocatedBytes(false) - allocation,
                        workingSetBytes = Environment.WorkingSet,
                        error
                    });
                }
            }
            string screenshot = Path.ChangeExtension(output, ".png");
            var bitmap = new RenderTargetBitmap((int)window.ActualWidth, (int)window.ActualHeight, 96, 96, PixelFormats.Pbgra32);
            bitmap.Render(window);
            var png = new PngBitmapEncoder(); png.Frames.Add(BitmapFrame.Create(bitmap));
            using (var stream = File.Create(screenshot)) png.Save(stream);
            bool unchanged = paths.All(file => Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(file.Path))) == file.Hash);
            passed &= unchanged;
            File.WriteAllText(output, JsonSerializer.Serialize(new
            {
                measuredUtc = DateTime.UtcNow,
                manifestPath,
                os = Environment.OSVersion.ToString(),
                runtime = Environment.Version.ToString(),
                processorCount = Environment.ProcessorCount,
                control = "Real shared FileDocumentPreview, offscreen window; not a main/Quick end-to-end task or physical keyboard/presentation measurement.",
                condition = "Same process; no cache flushing; 10 ms readiness polling plus application-idle layout. First invocation retained; warm samples exclude iteration zero. Database timing includes private snapshot/schema/page/grid. Heartbeat gaps proxy dispatcher responsiveness; sub-tick final intervals are retained.",
                scan = new { path = scanPath, sha256 = Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(scanPath))), provenance = "WPF rasterized text" },
                sourceBytesUnchanged = unchanged,
                passed,
                rows
            }, new JsonSerializerOptions { WriteIndented = true }));
        }
        finally { window.Close(); }
        if (!passed) throw new InvalidOperationException("Preview baseline recorded a failed case; inspect JSON.");
        if (includeOcr) await MeasureOcrAsync(scanPath, Path.ChangeExtension(output, ".ocr.json"), repeats);
    }

    private static async Task MeasureOcrAsync(string scanPath, string output, int repeats)
    {
        string[] languages = OcrEngine.AvailableRecognizerLanguages.Select(language => language.LanguageTag).ToArray();
        string? selected = languages.FirstOrDefault(tag => tag.Equals("en-US", StringComparison.OrdinalIgnoreCase)) ??
            languages.FirstOrDefault(tag => tag.StartsWith("en-", StringComparison.OrdinalIgnoreCase));
        if (selected is null)
        {
            File.WriteAllText(output, JsonSerializer.Serialize(new { status = "unavailable", availableLanguages = languages, reason = "No installed English Windows OCR recognizer for this English fixture; no download attempted." }));
            return;
        }
        var registry = new ExtractorRegistry([new WindowsImageOcrExtractor(new WindowsImageOcrOptions { OcrLanguageTag = selected })]);
        var walker = new FileWalker();
        var options = new WalkerOptions { IncludeExtensions = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { ".png" }, ExcludeDirectories = new HashSet<string>(StringComparer.OrdinalIgnoreCase) };
        string root = Path.GetDirectoryName(scanPath)!;
        using var index = new CSharpDbFileIndex(new FileIndexOptions { DatabasePath = output + ".index.db" }, walker, registry);
        var clock = Stopwatch.StartNew();
        // Index only this generated scan. Other result/output files in the parent are outside the extension filter.
        await index.UpsertFileAsync(root, scanPath, options, CancellationToken.None);
        double indexMs = clock.Elapsed.TotalMilliseconds;
        var searcher = new Searcher(walker, registry);
        var samples = new List<object>();
        bool passed = true;
        foreach (string route in new[] { "indexed", "live" })
        {
            for (int iteration = 0; iteration <= repeats; iteration++)
            {
                // Root is restricted to the exact filename to avoid counting older scan PNGs in the same report folder.
                var request = new SearchRequest(new TermQuery("Annual renewal approved"), [root], options with { IncludeGlobs = [Path.GetFileName(scanPath)] }, UseIndex: route == "indexed");
                clock.Restart();
                double? firstUsefulMs = null;
                var stream = route == "indexed" ? index.SearchAsync(request, CancellationToken.None) : searcher.SearchAsync(request, CancellationToken.None);
                await foreach (var hit in stream)
                    if (hit.Path == scanPath && hit.Anchor?.Kind == SourceAnchorKind.ImageOcr && hit.Anchor.Width > 0) firstUsefulMs ??= clock.Elapsed.TotalMilliseconds;
                bool success = firstUsefulMs.HasValue;
                passed &= success;
                samples.Add(new { route, iteration, success, firstUsefulMs, completeMs = clock.Elapsed.TotalMilliseconds });
            }
        }
        File.WriteAllText(output, JsonSerializer.Serialize(new
        {
            status = "measured",
            selectedLanguage = selected,
            availableLanguages = languages,
            scanPath,
            condition = "Generated English scan; same-process first invocation and warm samples; real Windows OCR with existing language pack; no model download. Only the expected scan and a nonempty OCR region count as useful.",
            indexMs,
            passed,
            samples
        }, new JsonSerializerOptions { WriteIndented = true }));
        if (!passed) throw new InvalidOperationException("OCR baseline recorded a failed case; inspect OCR JSON.");
    }

    private static double Percentile(List<double> values, double p) => values.Order().ElementAt(Math.Clamp((int)Math.Ceiling(values.Count * p) - 1, 0, values.Count - 1));
    private static IEnumerable<DependencyObject> Descendants(DependencyObject root)
    {
        for (int i = 0; i < VisualTreeHelper.GetChildrenCount(root); i++)
        {
            var child = VisualTreeHelper.GetChild(root, i);
            yield return child;
            foreach (var descendant in Descendants(child)) yield return descendant;
        }
    }
}
