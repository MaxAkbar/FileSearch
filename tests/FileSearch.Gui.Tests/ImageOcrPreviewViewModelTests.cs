using System.IO.Compression;
using System.Text;
using FileSearch.Core.Engine;
using FileSearch.Core.Extractors;
using FileSearch.Gui.ViewModels;

namespace FileSearch.Gui.Tests;

public sealed class ImageOcrPreviewViewModelTests
{
    [Fact]
    public void TryCreateReturnsPreviewForImageOcrAnchor()
    {
        var path = Path.Combine(Path.GetTempPath(), $"{Guid.NewGuid():N}.png");
        File.WriteAllText(path, "placeholder");
        try
        {
            var hit = new Hit(
                path,
                1,
                "needle",
                Array.Empty<FileSearch.Core.Queries.MatchSpan>(),
                Anchor: SourceAnchor.ImageOcrRegion(10, 20, 30, 40, 100, 200));

            var preview = ImageOcrPreviewViewModel.TryCreate(path, new[] { hit });

            Assert.NotNull(preview);
            Assert.Equal(path, preview.ImagePath);
            Assert.Equal(100, preview.SourceWidth);
            Assert.Equal(200, preview.SourceHeight);
            Assert.Equal(10, preview.X);
            Assert.Equal(20, preview.Y);
            Assert.Equal(30, preview.Width);
            Assert.Equal(40, preview.Height);
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Fact]
    public void TryCreateReturnsNullWithoutImageOcrAnchor()
    {
        var path = Path.Combine(Path.GetTempPath(), $"{Guid.NewGuid():N}.txt");
        File.WriteAllText(path, "placeholder");
        try
        {
            var hit = new Hit(path, 1, "needle", Array.Empty<FileSearch.Core.Queries.MatchSpan>());

            Assert.Null(ImageOcrPreviewViewModel.TryCreate(path, new[] { hit }));
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Fact]
    public async Task TryCreateAsyncReturnsPreviewForArchiveEmbeddedOcrAnchor()
    {
        var path = Path.Combine(Path.GetTempPath(), $"{Guid.NewGuid():N}.zip");
        var imageBytes = new byte[] { 1, 2, 3, 4, 5 };
        CreateZip(path, "images/scan.png", imageBytes);
        try
        {
            var hit = new Hit(
                path,
                1,
                "needle",
                Array.Empty<FileSearch.Core.Queries.MatchSpan>(),
                Anchor: SourceAnchor.EmbeddedOcrRegion(
                    SourceAnchorKind.Archive,
                    "archive member images/scan.png",
                    "images/scan.png",
                    1,
                    2,
                    3,
                    4,
                    10,
                    20));

            var preview = await ImageOcrPreviewViewModel.TryCreateAsync(path, new[] { hit }, TestContext.Current.CancellationToken);

            Assert.NotNull(preview);
            Assert.True(File.Exists(preview.ImagePath));
            Assert.Equal(imageBytes, await File.ReadAllBytesAsync(preview.ImagePath, TestContext.Current.CancellationToken));
            Assert.Equal(10, preview.SourceWidth);
            Assert.Equal(20, preview.SourceHeight);
            Assert.Equal(1, preview.X);
            Assert.Equal(2, preview.Y);
            Assert.Equal(3, preview.Width);
            Assert.Equal(4, preview.Height);
            Assert.Contains("images/scan.png", preview.Label);
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Fact]
    public async Task TryCreateAsyncFindsPackageMemberBySuffix()
    {
        var path = Path.Combine(Path.GetTempPath(), $"{Guid.NewGuid():N}.docx");
        var imageBytes = new byte[] { 6, 7, 8, 9 };
        CreateZip(path, "word/media/image1.png", imageBytes);
        try
        {
            var hit = new Hit(
                path,
                1,
                "needle",
                Array.Empty<FileSearch.Core.Queries.MatchSpan>(),
                Anchor: SourceAnchor.EmbeddedOcrRegion(
                    SourceAnchorKind.Word,
                    "image media/image1.png",
                    "media/image1.png",
                    5,
                    6,
                    7,
                    8,
                    100,
                    200));

            var preview = await ImageOcrPreviewViewModel.TryCreateAsync(path, new[] { hit }, TestContext.Current.CancellationToken);

            Assert.NotNull(preview);
            Assert.Equal(imageBytes, await File.ReadAllBytesAsync(preview.ImagePath, TestContext.Current.CancellationToken));
            Assert.Equal(5, preview.X);
            Assert.Equal(6, preview.Y);
            Assert.Equal(7, preview.Width);
            Assert.Equal(8, preview.Height);
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Fact]
    public async Task TryCreateAsyncReturnsPreviewForEmailEmbeddedOcrAnchor()
    {
        var path = Path.Combine(Path.GetTempPath(), $"{Guid.NewGuid():N}.eml");
        var imageBytes = Encoding.ASCII.GetBytes("fake-png-bytes");
        var content = string.Join(
            "\r\n",
            "From: sender@example.test",
            "Content-Type: multipart/mixed; boundary=\"boundary-1\"",
            string.Empty,
            "--boundary-1",
            "Content-Type: image/png; name=\"scan.png\"",
            "Content-Transfer-Encoding: base64",
            "Content-Disposition: attachment; filename=\"scan.png\"",
            string.Empty,
            Convert.ToBase64String(imageBytes),
            "--boundary-1--",
            string.Empty);
        File.WriteAllText(path, content, Encoding.ASCII);
        try
        {
            var hit = new Hit(
                path,
                1,
                "needle",
                Array.Empty<FileSearch.Core.Queries.MatchSpan>(),
                Anchor: SourceAnchor.EmbeddedOcrRegion(
                    SourceAnchorKind.Email,
                    "email image scan.png",
                    "scan.png",
                    3,
                    4,
                    5,
                    6,
                    50,
                    60));

            var preview = await ImageOcrPreviewViewModel.TryCreateAsync(path, new[] { hit }, TestContext.Current.CancellationToken);

            Assert.NotNull(preview);
            Assert.Equal(imageBytes, await File.ReadAllBytesAsync(preview.ImagePath, TestContext.Current.CancellationToken));
            Assert.Equal(50, preview.SourceWidth);
            Assert.Equal(60, preview.SourceHeight);
            Assert.Contains("scan.png", preview.Label);
        }
        finally
        {
            File.Delete(path);
        }
    }

    private static void CreateZip(string path, string memberPath, byte[] bytes)
    {
        using var archive = ZipFile.Open(path, ZipArchiveMode.Create);
        var entry = archive.CreateEntry(memberPath);
        using var stream = entry.Open();
        stream.Write(bytes);
    }
}
