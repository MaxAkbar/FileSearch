using System.IO;
using System.Linq;
using System.Threading;
using FileSearch.Core.Walker;
using Xunit;

namespace FileSearch.Core.Tests;

public sealed class WalkerTests : IDisposable
{
    private readonly string _root;

    public WalkerTests()
    {
        _root = Path.Combine(Path.GetTempPath(), "filesearch-tests-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_root);
    }

    public void Dispose()
    {
        try { Directory.Delete(_root, recursive: true); } catch { }
    }

    [Fact]
    public void Enumerate_ReturnsAllFiles_WhenRecursive()
    {
        File.WriteAllText(Path.Combine(_root, "a.txt"), "alpha");
        var sub = Directory.CreateDirectory(Path.Combine(_root, "sub")).FullName;
        File.WriteAllText(Path.Combine(sub, "b.txt"), "bravo");

        var walker = new FileWalker();
        var result = walker.Enumerate(new[] { _root }, new WalkerOptions(), CancellationToken.None).ToList();

        Assert.Equal(2, result.Count);
        Assert.Contains(result, p => p.EndsWith("a.txt"));
        Assert.Contains(result, p => p.EndsWith("b.txt"));
    }

    [Fact]
    public void Enumerate_AppliesIncludeGlobs()
    {
        File.WriteAllText(Path.Combine(_root, "a.cs"), "");
        File.WriteAllText(Path.Combine(_root, "b.txt"), "");

        var walker = new FileWalker();
        var result = walker.Enumerate(
            new[] { _root },
            new WalkerOptions { IncludeGlobs = new[] { "*.cs" } },
            CancellationToken.None).ToList();

        Assert.Single(result);
        Assert.EndsWith("a.cs", result[0]);
    }

    [Fact]
    public void Enumerate_AppliesExcludeGlobs()
    {
        File.WriteAllText(Path.Combine(_root, "a.cs"), "");
        File.WriteAllText(Path.Combine(_root, "b.txt"), "");

        var walker = new FileWalker();
        var result = walker.Enumerate(
            new[] { _root },
            new WalkerOptions { ExcludeGlobs = new[] { "*.txt" } },
            CancellationToken.None).ToList();

        Assert.Single(result);
        Assert.EndsWith("a.cs", result[0]);
    }

    [Fact]
    public void Enumerate_AppliesIncludeExtensions()
    {
        File.WriteAllText(Path.Combine(_root, "a.cs"), "");
        File.WriteAllText(Path.Combine(_root, "b.txt"), "");

        var walker = new FileWalker();
        var result = walker.Enumerate(
            new[] { _root },
            new WalkerOptions { IncludeExtensions = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { ".cs" } },
            CancellationToken.None).ToList();

        Assert.Single(result);
        Assert.EndsWith("a.cs", result[0]);
    }

    [Fact]
    public void Enumerate_AppliesExcludeExtensions()
    {
        File.WriteAllText(Path.Combine(_root, "a.cs"), "");
        File.WriteAllText(Path.Combine(_root, "b.txt"), "");

        var walker = new FileWalker();
        var result = walker.Enumerate(
            new[] { _root },
            new WalkerOptions { ExcludeExtensions = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { ".txt" } },
            CancellationToken.None).ToList();

        Assert.Single(result);
        Assert.EndsWith("a.cs", result[0]);
    }

    [Fact]
    public void Enumerate_PrunesDefaultExcludedDirectories()
    {
        File.WriteAllText(Path.Combine(_root, "app.txt"), "alpha");
        var modules = Directory.CreateDirectory(Path.Combine(_root, "node_modules", "dep")).FullName;
        File.WriteAllText(Path.Combine(modules, "index.js"), "bravo");

        var walker = new FileWalker();
        var result = walker.Enumerate(new[] { _root }, new WalkerOptions(), CancellationToken.None).ToList();

        Assert.Single(result);
        Assert.EndsWith("app.txt", result[0]);
    }

    [Fact]
    public void Enumerate_WalksExcludedDirectoriesWhenCleared()
    {
        var modules = Directory.CreateDirectory(Path.Combine(_root, "node_modules")).FullName;
        File.WriteAllText(Path.Combine(modules, "index.js"), "bravo");

        var walker = new FileWalker();
        var result = walker.Enumerate(
            new[] { _root },
            new WalkerOptions { ExcludeDirectories = new HashSet<string>(StringComparer.OrdinalIgnoreCase) },
            CancellationToken.None).ToList();

        Assert.Single(result);
        Assert.EndsWith("index.js", result[0]);
    }

    [Fact]
    public void Enumerate_AppliesIncludeDirectories()
    {
        var src = Directory.CreateDirectory(Path.Combine(_root, "src")).FullName;
        var tests = Directory.CreateDirectory(Path.Combine(_root, "tests")).FullName;
        File.WriteAllText(Path.Combine(src, "app.cs"), "");
        File.WriteAllText(Path.Combine(tests, "appTests.cs"), "");
        File.WriteAllText(Path.Combine(_root, "README.md"), "");

        var walker = new FileWalker();
        var result = walker.Enumerate(
            new[] { _root },
            new WalkerOptions
            {
                IncludeDirectories = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { "src" },
            },
            CancellationToken.None).ToList();

        Assert.Single(result);
        Assert.EndsWith("app.cs", result[0]);
    }

    [Fact]
    public void Enumerate_SkipsFilesLargerThanMax()
    {
        File.WriteAllText(Path.Combine(_root, "small.txt"), "x");
        File.WriteAllBytes(Path.Combine(_root, "big.txt"), new byte[2048]);

        var walker = new FileWalker();
        var result = walker.Enumerate(
            new[] { _root },
            new WalkerOptions { MaxFileSizeBytes = 100 },
            CancellationToken.None).ToList();

        Assert.Single(result);
        Assert.EndsWith("small.txt", result[0]);
    }

    [Fact]
    public void Enumerate_NonRecursive_OnlyIncludesTopLevel()
    {
        File.WriteAllText(Path.Combine(_root, "top.txt"), "");
        var sub = Directory.CreateDirectory(Path.Combine(_root, "sub")).FullName;
        File.WriteAllText(Path.Combine(sub, "nested.txt"), "");

        var walker = new FileWalker();
        var result = walker.Enumerate(
            new[] { _root },
            new WalkerOptions { Recursive = false },
            CancellationToken.None).ToList();

        Assert.Single(result);
        Assert.EndsWith("top.txt", result[0]);
    }

    [Fact]
    public void Enumerate_SkipsFilesSmallerThanMin()
    {
        File.WriteAllBytes(Path.Combine(_root, "small.txt"), new byte[10]);
        File.WriteAllBytes(Path.Combine(_root, "big.txt"), new byte[2048]);

        var walker = new FileWalker();
        var result = walker.Enumerate(
            new[] { _root },
            new WalkerOptions { MinFileSizeBytes = 1024 },
            CancellationToken.None).ToList();

        Assert.Single(result);
        Assert.EndsWith("big.txt", result[0]);
    }

    [Fact]
    public void Enumerate_AppliesModifiedAfterFilter()
    {
        var oldFile = Path.Combine(_root, "old.txt");
        var newFile = Path.Combine(_root, "new.txt");
        File.WriteAllText(oldFile, "");
        File.WriteAllText(newFile, "");
        File.SetLastWriteTimeUtc(oldFile, DateTime.UtcNow.AddDays(-30));
        File.SetLastWriteTimeUtc(newFile, DateTime.UtcNow);

        var walker = new FileWalker();
        var result = walker.Enumerate(
            new[] { _root },
            new WalkerOptions { ModifiedAfterUtc = DateTime.UtcNow.AddDays(-1) },
            CancellationToken.None).ToList();

        Assert.Single(result);
        Assert.EndsWith("new.txt", result[0]);
    }

    [Fact]
    public void Enumerate_AppliesModifiedBeforeFilter()
    {
        var oldFile = Path.Combine(_root, "old.txt");
        var newFile = Path.Combine(_root, "new.txt");
        File.WriteAllText(oldFile, "");
        File.WriteAllText(newFile, "");
        File.SetLastWriteTimeUtc(oldFile, DateTime.UtcNow.AddDays(-30));
        File.SetLastWriteTimeUtc(newFile, DateTime.UtcNow);

        var walker = new FileWalker();
        var result = walker.Enumerate(
            new[] { _root },
            new WalkerOptions { ModifiedBeforeUtc = DateTime.UtcNow.AddDays(-1) },
            CancellationToken.None).ToList();

        Assert.Single(result);
        Assert.EndsWith("old.txt", result[0]);
    }
}
