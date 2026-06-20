using FileSearch.Core.Engine;
using FileSearch.Core.Extractors;
using FileSearch.Core.Queries;
using FileSearch.Gui.ViewModels;

namespace FileSearch.Gui.Tests;

public sealed class FileResultViewModelTests
{
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
}
