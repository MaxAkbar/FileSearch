using System.Runtime.CompilerServices;
using FileSearch.Core.Engine;
using FileSearch.Core.Extractors;
using FileSearch.Core.Queries;
using FileSearch.Gui.Services;
using FileSearch.Gui.Settings;
using FileSearch.Gui.ViewModels;

namespace FileSearch.Gui.Tests;

/// <summary>
/// View-model flow tests enabled by the sub-viewmodel split and the pumping
/// synchronization context (the drain loop resumes on the captured context,
/// which the pump executes on the test thread).
/// </summary>
public sealed class SearchViewModelTests
{
    [Fact]
    public void SearchPopulatesResultsRecordsHistoryAndPersists()
    {
        RunWithPump((pump, vm, history, status, settings) =>
        {
            vm.QueryText = "needle";
            vm.SearchPath = Path.GetTempPath();

            var task = vm.SearchCommand.ExecuteAsync(null);
            pump.PumpUntil(() => task.IsCompleted, TimeSpan.FromSeconds(10));

            Assert.Equal(2, vm.Files.Count);
            Assert.Equal(3, vm.TotalHits);
            Assert.Equal(2, vm.FilesMatched);
            Assert.False(vm.IsSearching);
            Assert.NotEqual("—", vm.ElapsedText);
            Assert.StartsWith("Done", status.Text);
            Assert.Equal("needle", history.RecentQueries[0]);
            Assert.Equal("needle", history.SavedSearches[0].QueryText);
            Assert.Equal(Path.GetTempPath().TrimEnd(Path.DirectorySeparatorChar), history.SavedSearches[0].SearchPath.TrimEnd(Path.DirectorySeparatorChar));

            // History persists its slice immediately (crash safety).
            Assert.Contains("needle", settings.Current.RecentQueries);
            Assert.Contains(settings.Current.SavedSearches, search => search.QueryText == "needle");
        });
    }

    [Fact]
    public void LargeResultSetDrainsCompletelyAcrossBatches()
    {
        // 2,500 hits force more than one capped drain (2,000/tick) plus the
        // consumer-finished-but-queue-nonempty branch of the drain loop.
        RunWithPump((pump, vm, history, status, settings) =>
        {
            vm.QueryText = "needle";
            vm.SearchPath = Path.GetTempPath();

            var task = vm.SearchCommand.ExecuteAsync(null);
            pump.PumpUntil(() => task.IsCompleted, TimeSpan.FromSeconds(10));

            Assert.Equal(2500, vm.TotalHits);
            Assert.Single(vm.Files);
            Assert.StartsWith("Done", status.Text);
        }, new BulkSearcher(2500));
    }

    [Fact]
    public void InvalidQueryReportsAndStillRecordsHistory()
    {
        RunWithPump((pump, vm, history, status, settings) =>
        {
            vm.SearchMode = QueryMode.Regex;
            vm.QueryText = "(";
            vm.SearchPath = Path.GetTempPath();

            var task = vm.SearchCommand.ExecuteAsync(null);
            pump.PumpUntil(() => task.IsCompleted, TimeSpan.FromSeconds(10));

            Assert.StartsWith("Invalid query", status.Text);
            Assert.Empty(vm.Files);
            Assert.False(vm.IsSearching);

            // Documented contract: failing searches still land in history.
            Assert.Equal("(", history.RecentQueries[0]);
        });
    }

    [Fact]
    public void UnifiedQueryTextBuildsInterpretedChips()
    {
        RunWithPump((pump, vm, history, status, settings) =>
        {
            vm.SearchMode = QueryMode.Unified;
            vm.QueryText = "invoice type:pdf modified:last-year";

            Assert.True(vm.HasQueryChips);
            Assert.Contains(vm.QueryChips, chip => chip.Field == "Content" && chip.Value == "invoice");
            Assert.Contains(vm.QueryChips, chip => chip.Field == "Type" && chip.Value == "pdf");
            Assert.Contains(vm.QueryChips, chip => chip.Field == "Modified" && chip.Value == "last-year");
        });
    }

    [Fact]
    public void NonUnifiedModeClearsInterpretedChips()
    {
        RunWithPump((pump, vm, history, status, settings) =>
        {
            vm.SearchMode = QueryMode.Unified;
            vm.QueryText = "invoice type:pdf";
            Assert.NotEmpty(vm.QueryChips);

            vm.SearchMode = QueryMode.Regex;

            Assert.False(vm.HasQueryChips);
            Assert.Empty(vm.QueryChips);
        });
    }

    [Fact]
    public void RemoveQueryChipRemovesRawQueryToken()
    {
        RunWithPump((pump, vm, history, status, settings) =>
        {
            vm.SearchMode = QueryMode.Unified;
            vm.QueryText = "invoice type:pdf modified:last-year";
            var typeChip = Assert.Single(vm.QueryChips, chip => chip.Field == "Type");

            vm.RemoveQueryChipCommand.Execute(typeChip);

            Assert.Equal("invoice modified:last-year", vm.QueryText);
            Assert.DoesNotContain(vm.QueryChips, chip => chip.Field == "Type");
            Assert.Contains(vm.QueryChips, chip => chip.Field == "Content" && chip.Value == "invoice");
            Assert.Contains(vm.QueryChips, chip => chip.Field == "Modified" && chip.Value == "last-year");
        });
    }

    [Fact]
    public void EditingQueryChipValueRewritesRawQueryToken()
    {
        RunWithPump((pump, vm, history, status, settings) =>
        {
            vm.SearchMode = QueryMode.Unified;
            vm.QueryText = "invoice type:pdf modified:last-year";
            var typeChip = Assert.Single(vm.QueryChips, chip => chip.Field == "Type");

            typeChip.Value = "docx";

            Assert.Equal("invoice type:docx modified:last-year", vm.QueryText);
            Assert.Contains(vm.QueryChips, chip => chip.Field == "Type" && chip.Value == "docx");
            Assert.DoesNotContain(vm.QueryChips, chip => chip.Field == "Type" && chip.Value == "pdf");
        });
    }

    [Fact]
    public void EditingFuzzyQueryChipPreservesFuzzyOperator()
    {
        RunWithPump((pump, vm, history, status, settings) =>
        {
            vm.SearchMode = QueryMode.Unified;
            vm.QueryText = "invoice~2";
            var fuzzyChip = Assert.Single(vm.QueryChips, chip => chip.Field == "Fuzzy 2");

            fuzzyChip.Value = "receipt";

            Assert.Equal("receipt~2", vm.QueryText);
            Assert.Contains(vm.QueryChips, chip => chip.Field == "Fuzzy 2" && chip.Value == "receipt");
        });
    }

    [Fact]
    public void NaturalLanguageQueryTextBuildsEditableInterpretedChips()
    {
        RunWithPump((pump, vm, history, status, settings) =>
        {
            vm.SearchMode = QueryMode.Unified;
            vm.QueryText = "PDF invoices from Acme modified last summer";

            Assert.Contains(vm.QueryChips, chip => chip.Field == "Type" && chip.Value == "pdf");
            Assert.Contains(vm.QueryChips, chip => chip.Field == "Modified" && chip.Value == "last-summer");
            Assert.Contains(vm.QueryChips, chip => chip.Field == "Content" && chip.Value == "invoice");
            Assert.Contains(vm.QueryChips, chip => chip.Field == "Content" && chip.Value == "Acme");

            var modifiedChip = Assert.Single(vm.QueryChips, chip => chip.Field == "Modified");
            modifiedChip.Value = "last-month";

            Assert.Contains("modified:last-month", vm.QueryText);
        });
    }

    [Fact]
    public void SemanticQueryTextBuildsEditableChip()
    {
        RunWithPump((pump, vm, history, status, settings) =>
        {
            vm.SearchMode = QueryMode.Unified;
            vm.QueryText = "semantic:\"authentication migration\"";

            var chip = Assert.Single(vm.QueryChips);
            Assert.Equal("Semantic", chip.Field);
            Assert.Equal("authentication migration", chip.Value);
            Assert.True(chip.IsEnabled);
            Assert.False(chip.IsDisabled);
            Assert.True(string.IsNullOrEmpty(chip.Explanation));
        });
    }

    [Fact]
    public void SemanticSearchStartsAndDelegatesAvailabilityToSearcher()
    {
        var searcher = new RecordingSearcher();
        RunWithPump((pump, vm, history, status, settings) =>
        {
            vm.SearchMode = QueryMode.Unified;
            vm.QueryText = "semantic:\"authentication migration\"";
            vm.SearchPath = Path.GetTempPath();

            var task = vm.SearchCommand.ExecuteAsync(null);
            pump.PumpUntil(() => task.IsCompleted, TimeSpan.FromSeconds(10));

            Assert.False(vm.IsSearching);
            Assert.Equal(1, searcher.RequestCount);
            Assert.IsType<UnifiedQuery>(searcher.Request?.Expression);
        }, searcher);
    }

    [Fact]
    public void SearcherFailureReportsErrorAndResets()
    {
        RunWithPump((pump, vm, history, status, settings) =>
        {
            vm.QueryText = "needle";
            vm.SearchPath = Path.GetTempPath();

            var task = vm.SearchCommand.ExecuteAsync(null);
            pump.PumpUntil(() => task.IsCompleted, TimeSpan.FromSeconds(10));

            Assert.StartsWith("Error:", status.Text);
            Assert.Contains("boom", status.Text);
            Assert.False(vm.IsSearching);
        }, new FaultingSearcher());
    }

    [Fact]
    public void SecondSearchResetsPreviousResults()
    {
        RunWithPump((pump, vm, history, status, settings) =>
        {
            vm.QueryText = "needle";
            vm.SearchPath = Path.GetTempPath();

            var first = vm.SearchCommand.ExecuteAsync(null);
            pump.PumpUntil(() => first.IsCompleted, TimeSpan.FromSeconds(10));

            var second = vm.SearchCommand.ExecuteAsync(null);
            pump.PumpUntil(() => second.IsCompleted, TimeSpan.FromSeconds(10));

            // Counts equal a single run — stale results were cleared.
            Assert.Equal(2, vm.Files.Count);
            Assert.Equal(3, vm.TotalHits);
        });
    }

    [Fact]
    public void CancelStopsAnInfiniteStreamAndCommandsTrackState()
    {
        RunWithPump((pump, vm, history, status, settings) =>
        {
            vm.QueryText = "needle";
            vm.SearchPath = Path.GetTempPath();

            var task = vm.SearchCommand.ExecuteAsync(null);
            pump.PumpUntil(() => vm.TotalHits > 0, TimeSpan.FromSeconds(10));

            Assert.False(vm.SearchCommand.CanExecute(null));
            Assert.True(vm.CancelCommand.CanExecute(null));

            vm.CancelCommand.Execute(null);
            pump.PumpUntil(() => task.IsCompleted, TimeSpan.FromSeconds(10));

            Assert.StartsWith("Canceled", status.Text);
            Assert.False(vm.IsSearching);
            Assert.Equal("needle", history.RecentQueries[0]);
        }, new EndlessSearcher());
    }

    [Fact]
    public void OptionTogglesPersistTheirSettingsSliceEagerly()
    {
        RunWithPump((pump, vm, history, status, settings) =>
        {
            vm.UseIndex = true;
            Assert.True(settings.Current.UseIndex);

            vm.SkipUnknownFileTypes = true;
            Assert.True(settings.Current.SkipUnknownFileTypes);

            // Another slice's save must not clobber these values.
            history.RecordSearch(new SavedSearchSettings
            {
                QueryText = "query",
                SearchPath = Path.GetTempPath(),
            });
            Assert.True(settings.Current.UseIndex);
            Assert.True(settings.Current.SkipUnknownFileTypes);
            Assert.Contains("query", settings.Current.RecentQueries);
        });
    }

    [Fact]
    public void SavedSearchPreservesAndRestoresSearchProperties()
    {
        RunWithPump((pump, vm, history, status, settings) =>
        {
            var path = Path.GetTempPath();
            var after = new DateTime(2026, 1, 2);
            var before = new DateTime(2026, 2, 3);

            vm.QueryText = "needle";
            vm.SearchPath = path;
            vm.FileNamePattern = "*.cs; *.xaml";
            vm.ExcludeFileNamePattern = "*.g.cs";
            vm.IncludeSubfolders = false;
            vm.SearchMode = QueryMode.Regex;
            vm.SelectedSearchTargetOption = vm.SearchTargetOptions.Single(option => option.Value == SearchTarget.FileNames);
            vm.MatchCase = true;
            vm.EnableDocumentExtraction = false;
            vm.EnableImageOcr = true;
            vm.SkipUnknownFileTypes = true;
            vm.UseIndex = true;
            vm.MinSizeKB = 4;
            vm.MaxSizeKB = 128;
            vm.ModifiedAfterEnabled = true;
            vm.ModifiedAfter = after;
            vm.ModifiedBeforeEnabled = true;
            vm.ModifiedBefore = before;
            vm.AdditionalPlainTextExtensions = ".tmpl; .liquid";

            var task = vm.SearchCommand.ExecuteAsync(null);
            pump.PumpUntil(() => task.IsCompleted, TimeSpan.FromSeconds(10));

            var saved = Assert.Single(history.SavedSearches);
            Assert.Equal(path, saved.SearchPath);
            Assert.Equal(QueryMode.Regex, saved.SearchMode);
            Assert.Equal(SearchTarget.FileNames, saved.SearchTarget);
            Assert.False(saved.IncludeSubfolders);
            Assert.True(saved.MatchCase);
            Assert.False(saved.EnableDocumentExtraction);
            Assert.True(saved.EnableImageOcr);
            Assert.True(saved.SkipUnknownFileTypes);
            Assert.True(saved.UseIndex);
            Assert.Equal(4, saved.MinSizeKB);
            Assert.Equal(128, saved.MaxSizeKB);
            Assert.True(saved.ModifiedAfterEnabled);
            Assert.Equal(after, saved.ModifiedAfter);
            Assert.True(saved.ModifiedBeforeEnabled);
            Assert.Equal(before, saved.ModifiedBefore);
            Assert.Equal(".tmpl; .liquid", saved.AdditionalPlainTextExtensions);

            vm.QueryText = "other";
            vm.SearchPath = @"C:\Different";
            vm.FileNamePattern = "*.md";
            vm.ExcludeFileNamePattern = string.Empty;
            vm.IncludeSubfolders = true;
            vm.SearchMode = QueryMode.Boolean;
            vm.SelectedSearchTargetOption = vm.SearchTargetOptions.Single(option => option.Value == SearchTarget.Content);
            vm.MatchCase = false;
            vm.EnableDocumentExtraction = true;
            vm.EnableImageOcr = false;
            vm.SkipUnknownFileTypes = false;
            vm.UseIndex = false;
            vm.MinSizeKB = 0;
            vm.MaxSizeKB = 0;
            vm.ModifiedAfterEnabled = false;
            vm.ModifiedBeforeEnabled = false;
            vm.AdditionalPlainTextExtensions = string.Empty;

            vm.SelectedSavedSearch = saved;

            Assert.Equal("needle", vm.QueryText);
            Assert.Equal(path, vm.SearchPath);
            Assert.Equal("*.cs; *.xaml", vm.FileNamePattern);
            Assert.Equal("*.g.cs", vm.ExcludeFileNamePattern);
            Assert.False(vm.IncludeSubfolders);
            Assert.Equal(QueryMode.Regex, vm.SearchMode);
            Assert.Equal(SearchTarget.FileNames, vm.CurrentSearchTarget);
            Assert.True(vm.MatchCase);
            Assert.False(vm.EnableDocumentExtraction);
            Assert.True(vm.EnableImageOcr);
            Assert.True(vm.SkipUnknownFileTypes);
            Assert.True(vm.UseIndex);
            Assert.Equal(4, vm.MinSizeKB);
            Assert.Equal(128, vm.MaxSizeKB);
            Assert.True(vm.ModifiedAfterEnabled);
            Assert.Equal(after, vm.ModifiedAfter);
            Assert.True(vm.ModifiedBeforeEnabled);
            Assert.Equal(before, vm.ModifiedBefore);
            Assert.Equal(".tmpl; .liquid", vm.AdditionalPlainTextExtensions);
        });
    }

    [Fact]
    public void SearchAfterSavedSearchSelectionKeepsPathWhenRecentPathSelectionClears()
    {
        var searcher = new RecordingSearcher();
        RunWithPump((pump, vm, history, status, settings) =>
        {
            var folder1 = Path.Combine(Path.GetTempPath(), "folder1");
            var folder2 = Path.Combine(Path.GetTempPath(), "folder2");

            vm.SelectedSavedSearch = new SavedSearchSettings
            {
                QueryText = "folder1",
                SearchPath = folder1,
            };
            var first = vm.SearchCommand.ExecuteAsync(null);
            pump.PumpUntil(() => first.IsCompleted, TimeSpan.FromSeconds(10));

            vm.SelectedSavedSearch = new SavedSearchSettings
            {
                QueryText = "folder2",
                SearchPath = folder2,
            };

            // The recent-folder ListBox clears SelectedItem while its paged
            // Items collection refreshes. That null selection must not clear
            // the actual search path restored by the saved search.
            vm.SelectedRecentPath = null;

            var second = vm.SearchCommand.ExecuteAsync(null);
            pump.PumpUntil(() => second.IsCompleted, TimeSpan.FromSeconds(10));

            Assert.NotNull(searcher.Request);
            Assert.Equal(folder2, Assert.Single(searcher.Request.Roots));
            Assert.Equal(folder2, vm.SearchPath);
        }, searcher);
    }

    [Fact]
    public void SearchPassesIncludeAndExcludePatternsToWalkerOptions()
    {
        var searcher = new RecordingSearcher();
        RunWithPump((pump, vm, history, status, settings) =>
        {
            vm.QueryText = "needle";
            vm.SearchPath = Path.GetTempPath();
            vm.FileNamePattern = "*.cs; *.md";
            vm.ExcludeFileNamePattern = "*.g.cs, *.tmp";
            vm.EnableImageOcr = true;

            var task = vm.SearchCommand.ExecuteAsync(null);
            pump.PumpUntil(() => task.IsCompleted, TimeSpan.FromSeconds(10));

            Assert.NotNull(searcher.Request);
            Assert.Equal(new[] { "*.cs", "*.md" }, searcher.Request.WalkerOptions.IncludeGlobs);
            Assert.Equal(new[] { "*.g.cs", "*.tmp" }, searcher.Request.WalkerOptions.ExcludeGlobs);
            Assert.True(searcher.Request.WalkerOptions.EnableOcr);
        }, searcher);
    }

    [Fact]
    public void NameSearchPassesTargetAndIgnoresContentExtractionTypeFilters()
    {
        var searcher = new RecordingSearcher();
        RunWithPump((pump, vm, history, status, settings) =>
        {
            vm.QueryText = "needle";
            vm.SearchPath = Path.GetTempPath();
            vm.FileNamePattern = "*.cs";
            vm.ExcludeFileNamePattern = "*.tmp";
            vm.SkipUnknownFileTypes = true;
            vm.EnableDocumentExtraction = false;
            vm.EnableImageOcr = false;
            vm.SelectedSearchTargetOption = vm.SearchTargetOptions.Single(option => option.Value == SearchTarget.FileAndFolderNames);

            var task = vm.SearchCommand.ExecuteAsync(null);
            pump.PumpUntil(() => task.IsCompleted, TimeSpan.FromSeconds(10));

            Assert.NotNull(searcher.Request);
            Assert.Equal(SearchTarget.FileAndFolderNames, searcher.Request.SearchTarget);
            Assert.Equal(new[] { "*.cs" }, searcher.Request.WalkerOptions.IncludeGlobs);
            Assert.Equal(new[] { "*.tmp" }, searcher.Request.WalkerOptions.ExcludeGlobs);
            Assert.Empty(searcher.Request.WalkerOptions.IncludeExtensions);
            Assert.Empty(searcher.Request.WalkerOptions.ExcludeExtensions);
        }, searcher);
    }

    [Fact]
    public void ExcludeFileExtensionPatternCommandAppendsPatternAndRerunsSearch()
    {
        var searcher = new RecordingSearcher();
        RunWithPump((pump, vm, history, status, settings) =>
        {
            var result = new FileResultViewModel(@"C:\results\Component.CS", new FakeFileLauncher());
            vm.QueryText = "needle";
            vm.SearchPath = Path.GetTempPath();
            vm.ExcludeFileNamePattern = "*.log";

            Assert.True(vm.ExcludeFileExtensionPatternCommand.CanExecute(result));
            var task = vm.ExcludeFileExtensionPatternCommand.ExecuteAsync(result);
            pump.PumpUntil(() => task.IsCompleted, TimeSpan.FromSeconds(10));

            Assert.Equal("*.log; *.cs", vm.ExcludeFileNamePattern);
            Assert.NotNull(searcher.Request);
            Assert.Equal(new[] { "*.log", "*.cs" }, searcher.Request.WalkerOptions.ExcludeGlobs);
            Assert.Equal(1, searcher.RequestCount);

            var duplicate = vm.ExcludeFileExtensionPatternCommand.ExecuteAsync(result);
            pump.PumpUntil(() => duplicate.IsCompleted, TimeSpan.FromSeconds(10));

            Assert.Equal("*.log; *.cs", vm.ExcludeFileNamePattern);
            Assert.Equal(2, searcher.RequestCount);
        }, searcher);
    }

    [Fact]
    public void ResultSortAndFacetsFilterCurrentResults()
    {
        RunWithPump((pump, vm, history, status, settings) =>
        {
            vm.QueryText = "needle";
            vm.SearchPath = Path.GetTempPath();

            var task = vm.SearchCommand.ExecuteAsync(null);
            pump.PumpUntil(() => task.IsCompleted, TimeSpan.FromSeconds(10));

            vm.SelectedSortOption = vm.ResultSortOptions.Single(option => option.Value == ResultSortMode.HitCount);

            var visible = vm.FilesView.Cast<FileResultViewModel>().ToList();
            Assert.Equal(@"C:\results\a.cs", visible[0].FullPath);
            Assert.Equal(@"C:\results\b.md", visible[1].FullPath);

            vm.SelectedFileTypeFacet = vm.FileTypeFacetOptions.Single(option => option.Value == "md");

            visible = vm.FilesView.Cast<FileResultViewModel>().ToList();
            var result = Assert.Single(visible);
            Assert.Equal(@"C:\results\b.md", result.FullPath);
            Assert.Equal(1, vm.FilesVisible);
        }, new ResultManagementSearcher());
    }

    [Fact]
    public void SourceFacetUsesSearchRoute()
    {
        RunWithPump((pump, vm, history, status, settings) =>
        {
            vm.QueryText = "needle";
            vm.SearchPath = Path.GetTempPath();

            var task = vm.SearchCommand.ExecuteAsync(null);
            pump.PumpUntil(() => task.IsCompleted, TimeSpan.FromSeconds(10));

            Assert.Contains(vm.SourceFacetOptions, option => option.Label == "Indexed");
            Assert.Contains(vm.SourceFacetOptions, option => option.Label == "Live scan");

            vm.SelectedSourceFacet = vm.SourceFacetOptions.Single(option => option.Value == "Indexed");

            var visible = vm.FilesView.Cast<FileResultViewModel>().ToList();
            var result = Assert.Single(visible);
            Assert.Equal(@"C:\results\indexed.txt", result.FullPath);
        }, new MixedRouteSearcher());
    }

    [Fact]
    public void ExportResultsWritesJsonLinesForVisibleHits()
    {
        var exportPath = Path.Combine(Path.GetTempPath(), $"{Guid.NewGuid():N}.jsonl");
        var savePicker = new FakeFileSavePicker { PathToReturn = exportPath };
        try
        {
            RunWithPump((pump, vm, history, status, settings) =>
            {
                vm.QueryText = "needle";
                vm.SearchPath = Path.GetTempPath();

                var search = vm.SearchCommand.ExecuteAsync(null);
                pump.PumpUntil(() => search.IsCompleted, TimeSpan.FromSeconds(10));

                var export = vm.ExportResultsCommand.ExecuteAsync("jsonl");
                pump.PumpUntil(() => export.IsCompleted, TimeSpan.FromSeconds(10));

                Assert.Equal("Export search results", savePicker.LastTitle);
                Assert.EndsWith(".jsonl", savePicker.LastDefaultFileName);
                Assert.True(File.Exists(exportPath));
                var lines = File.ReadAllLines(exportPath);
                Assert.Equal(3, lines.Length);
                Assert.Contains("\"LineNumber\":1", lines[0]);
                Assert.Contains(@"C:\\results\\b.md", lines[0]);
                Assert.StartsWith("Exported", status.Text);
            }, new ResultManagementSearcher(), savePicker);
        }
        finally
        {
            if (File.Exists(exportPath))
                File.Delete(exportPath);
        }
    }

    [Fact]
    public void ExportResultsWritesStructuredSnippetFields()
    {
        var exportPath = Path.Combine(Path.GetTempPath(), $"{Guid.NewGuid():N}.jsonl");
        var savePicker = new FakeFileSavePicker { PathToReturn = exportPath };
        try
        {
            RunWithPump((pump, vm, history, status, settings) =>
            {
                vm.QueryText = "needle";
                vm.SearchPath = Path.GetTempPath();

                var search = vm.SearchCommand.ExecuteAsync(null);
                pump.PumpUntil(() => search.IsCompleted, TimeSpan.FromSeconds(10));

                var export = vm.ExportResultsCommand.ExecuteAsync("jsonl");
                pump.PumpUntil(() => export.IsCompleted, TimeSpan.FromSeconds(10));

                var line = Assert.Single(File.ReadAllLines(exportPath));
                Assert.Contains("\"Location\":\"page 2, line 8\"", line);
                Assert.Contains("\"Locator\":", line);
                Assert.Contains("\"Snippet\":", line);
                Assert.Contains("before context", line);
            }, new StructuredSnippetSearcher(), savePicker);
        }
        finally
        {
            if (File.Exists(exportPath))
                File.Delete(exportPath);
        }
    }

    [Fact]
    public void CopyGroundedAnswerCopiesCitationBackedMarkdown()
    {
        var launcher = new FakeFileLauncher();
        RunWithPump((pump, vm, history, status, settings) =>
        {
            vm.QueryText = "needle";
            vm.SearchPath = Path.GetTempPath();

            var search = vm.SearchCommand.ExecuteAsync(null);
            pump.PumpUntil(() => search.IsCompleted, TimeSpan.FromSeconds(10));

            Assert.True(vm.CopyGroundedAnswerCommand.CanExecute(null));
            vm.CopyGroundedAnswerCommand.Execute(null);

            Assert.NotNull(launcher.LastClipboardText);
            Assert.Contains("# Grounded Answer Draft", launcher.LastClipboardText, StringComparison.Ordinal);
            Assert.Contains(@"C:\results\structured.pdf", launcher.LastClipboardText, StringComparison.Ordinal);
            Assert.Contains("page 2, line 8", launcher.LastClipboardText, StringComparison.Ordinal);
            Assert.Contains("before context", launcher.LastClipboardText, StringComparison.Ordinal);
            Assert.StartsWith("Copied grounded answer draft", status.Text, StringComparison.Ordinal);
        }, new StructuredSnippetSearcher(), fileLauncher: launcher);
    }

    [Fact]
    public void TogglePinResultPersistsSharedPinnedPath()
    {
        RunWithPump((pump, vm, history, status, settings) =>
        {
            vm.QueryText = "needle";
            vm.SearchPath = Path.GetTempPath();

            var task = vm.SearchCommand.ExecuteAsync(null);
            pump.PumpUntil(() => task.IsCompleted, TimeSpan.FromSeconds(10));

            var result = vm.Files.Single(file => file.FullPath == @"C:\results\a.cs");

            vm.TogglePinResultCommand.Execute(result);

            Assert.True(result.IsPinned);
            Assert.Equal(result.FullPath, Assert.Single(settings.Current.QuickSearchPinnedPaths));

            vm.TogglePinResultCommand.Execute(result);

            Assert.False(result.IsPinned);
            Assert.Empty(settings.Current.QuickSearchPinnedPaths);
        }, new ResultManagementSearcher());
    }

    [Fact]
    public void ToggleFavoriteResultPersistsFavoriteAndUpdatesVisibleResult()
    {
        RunWithPump((pump, vm, history, status, settings) =>
        {
            vm.QueryText = "needle";
            vm.SearchPath = Path.GetTempPath();

            var task = vm.SearchCommand.ExecuteAsync(null);
            pump.PumpUntil(() => task.IsCompleted, TimeSpan.FromSeconds(10));

            var result = vm.Files.Single(file => file.FullPath == @"C:\results\a.cs");

            vm.ToggleFavoriteResultCommand.Execute(result);

            Assert.True(result.IsFavorite);
            Assert.Equal(@"C:\results\a.cs", Assert.Single(history.FavoriteResults).Path);
            Assert.Equal(@"C:\results\a.cs", Assert.Single(settings.Current.FavoriteResults).Path);

            vm.ToggleFavoriteResultCommand.Execute(result);

            Assert.False(result.IsFavorite);
            Assert.Empty(history.FavoriteResults);
            Assert.Empty(settings.Current.FavoriteResults);
        }, new ResultManagementSearcher());
    }

    [Fact]
    public void RemovingFavoriteFromSidebarUpdatesVisibleResult()
    {
        RunWithPump((pump, vm, history, status, settings) =>
        {
            vm.QueryText = "needle";
            vm.SearchPath = Path.GetTempPath();

            var task = vm.SearchCommand.ExecuteAsync(null);
            pump.PumpUntil(() => task.IsCompleted, TimeSpan.FromSeconds(10));

            var result = vm.Files.Single(file => file.FullPath == @"C:\results\a.cs");
            vm.ToggleFavoriteResultCommand.Execute(result);

            history.RemoveFavoriteResultCommand.Execute(history.FavoriteResults[0]);

            Assert.False(result.IsFavorite);
            Assert.Empty(history.FavoriteResults);
        }, new ResultManagementSearcher());
    }

    [Fact]
    public void OpenFavoriteResultUsesFileLauncher()
    {
        var launcher = new FakeFileLauncher();
        RunWithPump((pump, vm, history, status, settings) =>
        {
            var favorite = new FavoriteResultSettings { Path = @"C:\results\a.cs" };

            var task = vm.OpenFavoriteResultCommand.ExecuteAsync(favorite);
            pump.PumpUntil(() => task.IsCompleted, TimeSpan.FromSeconds(10));

            Assert.Equal(@"C:\results\a.cs", launcher.LastOpenedPath);
        }, fileLauncher: launcher);
    }

    [Fact]
    public void SaveAndApplyWorkspaceRestoresSearchViewAndSharedLists()
    {
        RunWithPump((pump, vm, history, status, settings) =>
        {
            vm.QueryText = "needle";
            vm.SearchPath = Path.GetTempPath();
            vm.FileNamePattern = "*.cs";
            vm.ExcludeFileNamePattern = "*.g.cs";
            vm.SearchMode = QueryMode.Regex;
            vm.MatchCase = true;
            vm.SelectedSortOption = vm.ResultSortOptions.Single(option => option.Value == ResultSortMode.HitCount);
            vm.SelectedGroupOption = vm.ResultGroupOptions.Single(option => option.Value == ResultGroupMode.Folder);
            history.SaveCustomScope("Source", "*.cs");
            settings.Current.QuickSearchSelectedIndexedRoots.Add(@"C:\Indexed");

            var search = vm.SearchCommand.ExecuteAsync(null);
            pump.PumpUntil(() => search.IsCompleted, TimeSpan.FromSeconds(10));
            vm.RefinementQuery = "code";
            var result = vm.Files.Single(file => file.FullPath == @"C:\results\a.cs");
            vm.ToggleFavoriteResultCommand.Execute(result);
            vm.TogglePinResultCommand.Execute(result);

            vm.SaveWorkspaceCommand.Execute(null);

            var workspace = Assert.Single(history.Workspaces);
            Assert.Contains("needle", workspace.Name);
            Assert.Equal("HitCount", workspace.ResultSort);
            Assert.Equal("Folder", workspace.ResultGroup);
            Assert.Equal("code", workspace.RefinementQuery);
            Assert.Equal(@"C:\Indexed", Assert.Single(workspace.QuickSearchSelectedIndexedRoots));
            Assert.Equal(@"C:\results\a.cs", Assert.Single(workspace.PinnedPaths));
            Assert.Equal(@"C:\results\a.cs", Assert.Single(workspace.FavoriteResults).Path);

            vm.QueryText = "other";
            vm.SearchPath = @"C:\Other";
            vm.FileNamePattern = "*.md";
            vm.ExcludeFileNamePattern = string.Empty;
            vm.SearchMode = QueryMode.Boolean;
            vm.MatchCase = false;
            vm.RefinementQuery = string.Empty;
            vm.SelectedSortOption = vm.ResultSortOptions.Single(option => option.Value == ResultSortMode.Relevance);
            vm.SelectedGroupOption = vm.ResultGroupOptions.Single(option => option.Value == ResultGroupMode.File);
            history.ReplaceCustomScopes(Array.Empty<SearchScope>());
            history.ReplaceFavoriteResults(Array.Empty<FavoriteResultSettings>());
            settings.Current.QuickSearchPinnedPaths.Clear();
            settings.Current.QuickSearchSelectedIndexedRoots.Clear();

            vm.SelectedWorkspace = workspace;

            Assert.Equal("needle", vm.QueryText);
            Assert.Equal(Path.GetTempPath(), vm.SearchPath);
            Assert.Equal("*.cs", vm.FileNamePattern);
            Assert.Equal("*.g.cs", vm.ExcludeFileNamePattern);
            Assert.Equal(QueryMode.Regex, vm.SearchMode);
            Assert.True(vm.MatchCase);
            Assert.Equal("code", vm.RefinementQuery);
            Assert.Equal(ResultSortMode.HitCount, vm.SelectedSortOption?.Value);
            Assert.Equal(ResultGroupMode.Folder, vm.SelectedGroupOption?.Value);
            Assert.Equal("Source", Assert.Single(history.CustomScopes).Name);
            Assert.Equal(@"C:\results\a.cs", Assert.Single(history.FavoriteResults).Path);
            Assert.Equal(@"C:\results\a.cs", Assert.Single(settings.Current.QuickSearchPinnedPaths));
            Assert.Equal(@"C:\Indexed", Assert.Single(settings.Current.QuickSearchSelectedIndexedRoots));
            Assert.True(result.IsFavorite);
            Assert.True(result.IsPinned);
            Assert.StartsWith("Workspace loaded", status.Text);
        }, new ResultManagementSearcher());
    }

    [Fact]
    public void WorkspaceRunOnLoadStartsSearchAfterRestore()
    {
        var searcher = new RecordingSearcher();

        RunWithPump((pump, vm, history, status, settings) =>
        {
            history.SaveWorkspace(new WorkspaceSettings
            {
                Name = "Daily Source",
                RunOnLoad = true,
                Search = new SavedSearchSettings
                {
                    QueryText = "needle",
                    SearchPath = Path.GetTempPath(),
                    FileNamePattern = "*.cs",
                },
            });

            vm.SelectedWorkspace = Assert.Single(history.Workspaces);

            pump.PumpUntil(() => searcher.RequestCount == 1 && !vm.IsSearching, TimeSpan.FromSeconds(10));

            Assert.Equal(1, searcher.RequestCount);
            Assert.Equal("needle", vm.QueryText);
            Assert.Equal(Path.GetTempPath(), vm.SearchPath);
            Assert.Equal("*.cs", vm.FileNamePattern);
            Assert.StartsWith("Done", status.Text);
        }, searcher);
    }

    [Fact]
    public void RenameResultUpdatesResultPathHitsAndPinnedPath()
    {
        var operations = new FakeFileOperationService
        {
            RenameResult = FileOperationResult.Renamed(@"C:\results\renamed.cs"),
        };

        RunWithPump((pump, vm, history, status, settings) =>
        {
            vm.QueryText = "needle";
            vm.SearchPath = Path.GetTempPath();

            var search = vm.SearchCommand.ExecuteAsync(null);
            pump.PumpUntil(() => search.IsCompleted, TimeSpan.FromSeconds(10));

            var result = vm.Files.Single(file => file.FullPath == @"C:\results\a.cs");
            vm.TogglePinResultCommand.Execute(result);
            vm.ToggleFavoriteResultCommand.Execute(result);

            var rename = vm.RenameResultCommand.ExecuteAsync(result);
            pump.PumpUntil(() => rename.IsCompleted, TimeSpan.FromSeconds(10));

            Assert.Equal(1, operations.RenameCallCount);
            Assert.Equal(@"C:\results\a.cs", operations.RenamePath);
            Assert.Equal(@"C:\results\renamed.cs", result.FullPath);
            Assert.Equal("renamed.cs", result.FileName);
            Assert.All(result.Hits, hit => Assert.Equal(@"C:\results\renamed.cs", hit.Path));
            Assert.Equal(@"C:\results\renamed.cs", Assert.Single(settings.Current.QuickSearchPinnedPaths));
            Assert.Equal(@"C:\results\renamed.cs", Assert.Single(history.FavoriteResults).Path);
            Assert.Equal("Renamed file.", status.Text);
        }, new ResultManagementSearcher(), fileOperationService: operations);
    }

    [Fact]
    public void DeleteResultRemovesResultAndPinnedPath()
    {
        var operations = new FakeFileOperationService
        {
            DeleteResult = FileOperationResult.Deleted(),
        };

        RunWithPump((pump, vm, history, status, settings) =>
        {
            vm.QueryText = "needle";
            vm.SearchPath = Path.GetTempPath();

            var search = vm.SearchCommand.ExecuteAsync(null);
            pump.PumpUntil(() => search.IsCompleted, TimeSpan.FromSeconds(10));

            var result = vm.Files.Single(file => file.FullPath == @"C:\results\a.cs");
            vm.TogglePinResultCommand.Execute(result);
            vm.ToggleFavoriteResultCommand.Execute(result);
            vm.SelectedFile = result;

            var delete = vm.DeleteResultCommand.ExecuteAsync(result);
            pump.PumpUntil(() => delete.IsCompleted, TimeSpan.FromSeconds(10));

            Assert.Equal(1, operations.DeleteCallCount);
            Assert.Equal(@"C:\results\a.cs", operations.DeletePath);
            Assert.DoesNotContain(vm.Files, file => file.FullPath == @"C:\results\a.cs");
            Assert.Single(vm.Files);
            Assert.Equal(1, vm.TotalHits);
            Assert.Equal(1, vm.FilesMatched);
            Assert.Null(vm.SelectedFile);
            Assert.Empty(settings.Current.QuickSearchPinnedPaths);
            Assert.Empty(history.FavoriteResults);
            Assert.Equal("Moved file to Recycle Bin.", status.Text);
        }, new ResultManagementSearcher(), fileOperationService: operations);
    }

    [Fact]
    public async Task FileResultOpenCommandRecordsUsage()
    {
        var recordedPaths = new List<string>();
        var result = new FileResultViewModel(
            @"C:\results\Component.cs",
            new FakeFileLauncher(),
            (path, _) =>
            {
                recordedPaths.Add(path);
                return Task.CompletedTask;
            });

        await result.OpenCommand.ExecuteAsync(null);

        Assert.Equal(@"C:\results\Component.cs", Assert.Single(recordedPaths));
    }

    private static void RunWithPump(
        Action<PumpingSynchronizationContext, SearchViewModel, HistoryViewModel, StatusBarViewModel, FakeSettingsService> body,
        ISearcher? searcher = null,
        IFileSavePicker? fileSavePicker = null,
        IFileOperationService? fileOperationService = null,
        IFileLauncher? fileLauncher = null)
    {
        var previous = SynchronizationContext.Current;
        var pump = new PumpingSynchronizationContext();
        SynchronizationContext.SetSynchronizationContext(pump);
        try
        {
            var status = new StatusBarViewModel();
            var settings = new FakeSettingsService();
            var appSettings = new ApplicationSettingsViewModel(settings, status);
            var history = new HistoryViewModel(settings, appSettings, status);
            var vm = new SearchViewModel(
                searcher ?? new StubSearcher(),
                new ExtractorRegistry(Array.Empty<ITextExtractor>()),
                new QueryFactory(),
                new FakePreviewService(),
                fileLauncher ?? new FakeFileLauncher(),
                settings,
                new FakeFileTypeOptionsStore(),
                new FakeFolderPicker(),
                history,
                status,
                fileSavePicker: fileSavePicker,
                fileOperationService: fileOperationService);

            body(pump, vm, history, status, settings);
        }
        finally
        {
            SynchronizationContext.SetSynchronizationContext(previous);
        }
    }

    /// <summary>Yields three hits across two files, then completes.</summary>
    private sealed class StubSearcher : ISearcher
    {
        public async IAsyncEnumerable<Hit> SearchAsync(
            SearchRequest request,
            [EnumeratorCancellation] CancellationToken cancellationToken)
        {
            await Task.Yield();
            yield return new Hit(@"C:\results\a.txt", 1, "needle one", Array.Empty<MatchSpan>());
            yield return new Hit(@"C:\results\a.txt", 2, "needle two", Array.Empty<MatchSpan>());
            yield return new Hit(@"C:\results\b.txt", 1, "needle three", Array.Empty<MatchSpan>());
        }
    }

    /// <summary>Yields N hits for one file as fast as possible.</summary>
    private sealed class BulkSearcher : ISearcher
    {
        private readonly int _count;

        public BulkSearcher(int count) => _count = count;

        public async IAsyncEnumerable<Hit> SearchAsync(
            SearchRequest request,
            [EnumeratorCancellation] CancellationToken cancellationToken)
        {
            await Task.Yield();
            for (var i = 1; i <= _count; i++)
                yield return new Hit(@"C:\results\bulk.txt", i, "needle", Array.Empty<MatchSpan>());
        }
    }

    /// <summary>Yields one hit, then throws.</summary>
    private sealed class FaultingSearcher : ISearcher
    {
        public async IAsyncEnumerable<Hit> SearchAsync(
            SearchRequest request,
            [EnumeratorCancellation] CancellationToken cancellationToken)
        {
            await Task.Yield();
            yield return new Hit(@"C:\results\a.txt", 1, "needle", Array.Empty<MatchSpan>());
            throw new InvalidOperationException("boom");
        }
    }

    private sealed class RecordingSearcher : ISearcher
    {
        public SearchRequest? Request { get; private set; }
        public int RequestCount { get; private set; }

        public async IAsyncEnumerable<Hit> SearchAsync(
            SearchRequest request,
            [EnumeratorCancellation] CancellationToken cancellationToken)
        {
            Request = request;
            RequestCount++;
            await Task.Yield();
            yield break;
        }
    }

    private sealed class ResultManagementSearcher : ISearcher
    {
        public async IAsyncEnumerable<Hit> SearchAsync(
            SearchRequest request,
            [EnumeratorCancellation] CancellationToken cancellationToken)
        {
            await Task.Yield();
            yield return new Hit(
                @"C:\results\b.md",
                1,
                "needle markdown",
                Array.Empty<MatchSpan>(),
                HitKind.Metadata,
                Score: 0.5,
                SizeBytes: 200 * 1024,
                ModifiedUtc: DateTime.UtcNow.AddDays(-2));
            yield return new Hit(
                @"C:\results\a.cs",
                1,
                "needle code one",
                Array.Empty<MatchSpan>(),
                HitKind.Content,
                Score: 0.4,
                SizeBytes: 20 * 1024,
                ModifiedUtc: DateTime.UtcNow);
            yield return new Hit(
                @"C:\results\a.cs",
                2,
                "needle code two",
                Array.Empty<MatchSpan>(),
                HitKind.Content,
                Score: 0.3,
                SizeBytes: 20 * 1024,
                ModifiedUtc: DateTime.UtcNow);
        }
    }

    private sealed class StructuredSnippetSearcher : ISearcher
    {
        public async IAsyncEnumerable<Hit> SearchAsync(
            SearchRequest request,
            [EnumeratorCancellation] CancellationToken cancellationToken)
        {
            await Task.Yield();
            var locator = new SourceLocator(Page: 2, StartLine: 8, EndLine: 8);
            yield return new Hit(
                @"C:\results\structured.pdf",
                8,
                "needle appears here",
                Array.Empty<MatchSpan>(),
                HitKind.Content,
                Locator: locator,
                Snippet: new SearchSnippet(
                    "before context\nneedle appears here\nafter context",
                    locator: locator,
                    contentUnitId: 77,
                    contentUnitIds: new long[] { 76, 77, 78 }));
        }
    }

    private sealed class MixedRouteSearcher : ISearcher
    {
        public async IAsyncEnumerable<Hit> SearchAsync(
            SearchRequest request,
            [EnumeratorCancellation] CancellationToken cancellationToken)
        {
            await Task.Yield();
            yield return new Hit(
                @"C:\results\indexed.txt",
                1,
                "needle indexed",
                Array.Empty<MatchSpan>(),
                HitKind.Content,
                Route: HitRoute.Indexed);
            yield return new Hit(
                @"C:\results\live.txt",
                1,
                "needle live",
                Array.Empty<MatchSpan>(),
                HitKind.Metadata,
                Route: HitRoute.Live);
        }
    }

    /// <summary>Streams hits forever until the token cancels.</summary>
    private sealed class EndlessSearcher : ISearcher
    {
        public async IAsyncEnumerable<Hit> SearchAsync(
            SearchRequest request,
            [EnumeratorCancellation] CancellationToken cancellationToken)
        {
            var line = 0;
            while (true)
            {
                cancellationToken.ThrowIfCancellationRequested();
                yield return new Hit(@"C:\results\stream.txt", ++line, "needle", Array.Empty<MatchSpan>());
                await Task.Delay(10, cancellationToken).ConfigureAwait(false);
            }
        }
    }
}
