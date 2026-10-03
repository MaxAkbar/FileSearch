using FileSearch.Core.Engine;
using FileSearch.Core.Extractors;
using FileSearch.Core.Queries;
using FileSearch.Gui.ViewModels;

namespace FileSearch.Gui.Tests;

public sealed class FileResultViewModelTests
{
    [Fact]
    public void SuppliedMetadataIsCachedWithoutAFileSystemRead()
    {
        var result = new FileResultViewModel(@"Z:\unavailable\needle.txt", new FakeFileLauncher(), isDirectory: false);
        var modified = new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc);
        result.AddHit(new Hit(result.FullPath, 1, "needle", [], SizeBytes: 2048, ModifiedUtc: modified));
        Assert.Equal(2048, result.SizeBytes);
        Assert.Equal(modified, result.ModifiedUtc);
        Assert.Equal("2 KB", result.SizeText);
        Assert.NotEqual("—", result.ModifiedText);
    }

    [Fact]
    public void MissingMetadataLoadsOffThreadAndNotifiesOnTheOwningContext()
    {
        var path = Path.GetTempFileName();
        File.WriteAllText(path, "needle");
        var previous = SynchronizationContext.Current;
        var pump = new PumpingSynchronizationContext();
        SynchronizationContext.SetSynchronizationContext(pump);
        try
        {
            var ownerThread = Environment.CurrentManagedThreadId;
            var result = new FileResultViewModel(path, new FakeFileLauncher(), isDirectory: false);
            Assert.Null(result.SizeBytes);
            Assert.Null(result.ModifiedUtc);
            var loaded = false;
            result.MetadataLoaded += (_, _) =>
            {
                Assert.Equal(ownerThread, Environment.CurrentManagedThreadId);
                loaded = true;
            };
            result.EnsureMetadataLoading();
            // The continuation is queued to the owner, so the getter cannot
            // synchronously open the file while sorting or evaluating facets.
            Assert.Null(result.SizeBytes);
            pump.PumpUntil(() => loaded, TimeSpan.FromSeconds(10));
            Assert.Equal(6, result.SizeBytes);
            Assert.NotNull(result.ModifiedUtc);
        }
        finally
        {
            SynchronizationContext.SetSynchronizationContext(previous);
            File.Delete(path);
        }
    }

    [Fact]
    public void BuildStoredHitPreviewIncludesAnchors()
    {
        var result = new FileResultViewModel(@"C:\docs\scan.pdf", new FakeFileLauncher());
        result.AddHit(new Hit(
            @"C:\docs\scan.pdf",
            2,
            "invoice total",
            Array.Empty<MatchSpan>(),
            HitKind.Content,
            Anchor: SourceAnchor.PdfOcrRegion(3, 10, 20, 30, 40, 100, 200)));

        var preview = result.BuildStoredHitPreview();

        Assert.Contains("invoice total", preview);
        Assert.Contains("page 3 OCR region x10 y20 30x40 of 100x200", preview);
    }

    [Fact]
    public void BuildStoredHitPreviewPrefersStructuredSnippet()
    {
        var locator = new SourceLocator(Page: 2, StartLine: 8, EndLine: 8);
        var snippet = new SearchSnippet(
            "before context\nneedle appears here\nafter context",
            locator: locator,
            contentUnitId: 99,
            contentUnitIds: new long[] { 98, 99, 100 });
        var result = new FileResultViewModel(@"C:\docs\report.pdf", new FakeFileLauncher());
        result.AddHit(new Hit(
            @"C:\docs\report.pdf",
            8,
            "needle appears here",
            Array.Empty<MatchSpan>(),
            HitKind.Content,
            Locator: locator,
            Snippet: snippet));

        var preview = result.BuildStoredHitPreview();

        Assert.True(result.HasStructuredSnippets);
        Assert.Contains("[page 2, line 8]", preview);
        Assert.Contains("before context", preview);
        Assert.Contains("after context", preview);
    }

    [Fact]
    public void PreviewHitsAreCappedIndependentlyFromResultCardExpansion()
    {
        var result = new FileResultViewModel(@"C:\docs\report.cs", new FakeFileLauncher());
        for (var i = 1; i <= FileResultViewModel.PreviewHitLimit + 2; i++)
        {
            result.AddHit(new Hit(
                @"C:\docs\report.cs",
                i,
                $"needle {i}",
                Array.Empty<MatchSpan>()));
        }

        Assert.True(result.HasPreviewHits);
        Assert.Equal(FileResultViewModel.PreviewHitLimit, result.PreviewHits.Count());
        Assert.True(result.HasMorePreviewHits);
        Assert.Equal(2, result.ExtraPreviewHitCount);
        Assert.Contains("+ 2 more matches", result.PreviewMoreText);

        result.ToggleExpandCommand.Execute(null);

        Assert.True(result.IsExpanded);
        Assert.Equal(FileResultViewModel.PreviewHitLimit, result.PreviewHits.Count());
    }

    [Fact]
    public async Task OpenImageOcrPreviewCommandLaunchesPreviewWhenAnchorIsAvailable()
    {
        var path = Path.Combine(Path.GetTempPath(), $"{Guid.NewGuid():N}.png");
        File.WriteAllText(path, "placeholder");
        try
        {
            var launcher = new FakeFileLauncher();
            var result = new FileResultViewModel(path, launcher);
            result.AddHit(new Hit(
                path,
                1,
                "image needle",
                Array.Empty<MatchSpan>(),
                Anchor: SourceAnchor.ImageOcrRegion(1, 2, 3, 4, 10, 20)));

            Assert.True(result.HasImageOcrPreview);
            Assert.True(result.OpenImageOcrPreviewCommand.CanExecute(null));

            await result.OpenImageOcrPreviewCommand.ExecuteAsync(null);

            Assert.NotNull(launcher.LastImageOcrPreview);
            Assert.Equal(path, launcher.LastImageOcrPreview.ImagePath);
            Assert.Equal(1, launcher.LastImageOcrPreview.X);
            Assert.Equal(2, launcher.LastImageOcrPreview.Y);
            Assert.Equal(3, launcher.LastImageOcrPreview.Width);
            Assert.Equal(4, launcher.LastImageOcrPreview.Height);
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Fact]
    public async Task OpenCommandUsesSourceAwareLauncherWhenAvailable()
    {
        var launcher = new FakeFileLauncher { OpenAtLocationResult = true };
        var result = new FileResultViewModel(@"C:\docs\report.cs", launcher);
        result.AddHit(new Hit(
            @"C:\docs\report.cs",
            12,
            "needle",
            Array.Empty<MatchSpan>(),
            Locator: new SourceLocator(StartLine: 12, EndLine: 12)));

        await result.OpenCommand.ExecuteAsync(null);

        Assert.Equal(@"C:\docs\report.cs", launcher.LastOpenedAtPath);
        Assert.NotNull(launcher.LastOpenedAtHit);
        Assert.Null(launcher.LastOpenedPath);
    }

    [Fact]
    public async Task OpenCommandFallsBackToFileOpenWhenSourceTargetUnavailable()
    {
        var launcher = new FakeFileLauncher { OpenAtLocationResult = false };
        var result = new FileResultViewModel(@"C:\docs\report.cs", launcher);
        result.AddHit(new Hit(
            @"C:\docs\report.cs",
            12,
            "needle",
            Array.Empty<MatchSpan>(),
            Locator: new SourceLocator(StartLine: 12, EndLine: 12)));

        await result.OpenCommand.ExecuteAsync(null);

        Assert.Equal(@"C:\docs\report.cs", launcher.LastOpenedAtPath);
        Assert.Equal(@"C:\docs\report.cs", launcher.LastOpenedPath);
    }
}
