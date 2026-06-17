using System.Linq;
using FileSearch.Core.Engine;
using FileSearch.Core.Extractors;
using FileSearch.Core.Indexing;
using FileSearch.Core.Queries;
using FileSearch.Core.Walker;
using FileSearch.Core.Workflows;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Logging;

namespace FileSearch.Core;

public static class ServiceCollectionExtensions
{
    /// <summary>
    /// Registers the core file-search services with the dependency injection
    /// container. Consumers can add additional <see cref="ITextExtractor"/>
    /// implementations (e.g., PDF, DOCX) by calling
    /// <c>services.AddSingleton&lt;ITextExtractor, MyExtractor&gt;()</c> before
    /// resolving <see cref="ISearcher"/>.
    /// </summary>
    public static IServiceCollection AddFileSearchCore(this IServiceCollection services)
    {
        services.TryAddSingleton<IFileWalker, FileWalker>();
        services.TryAddSingleton<IQueryParser>(_ => new QueryParser());
        services.TryAddSingleton<IQueryFactory, QueryFactory>();

        services.AddSingleton<ITextExtractor, PlainTextExtractor>();
        services.AddSingleton<ITextExtractor, PdfExtractor>();
        services.AddSingleton<ITextExtractor, WordExtractor>();
        services.AddSingleton<ITextExtractor, ExcelExtractor>();
        services.AddSingleton<ITextExtractor, PowerPointExtractor>();
        services.AddSingleton<ITextExtractor, OpenDocumentExtractor>();
        services.AddSingleton<ITextExtractor, EpubExtractor>();
        services.AddSingleton<ITextExtractor, RtfExtractor>();
        services.AddSingleton<ITextExtractor, HtmlExtractor>();
        services.AddSingleton<ITextExtractor, EmlExtractor>();
        services.AddSingleton<ITextExtractor, XmlTextExtractor>();
        services.AddSingleton<ITextExtractor, CalendarContactExtractor>();
        services.AddSingleton<ITextExtractor, ZipExtractor>();

        services.TryAddSingleton<IExtractorRegistry>(sp =>
        {
            var extractors = sp.GetServices<ITextExtractor>().ToArray();
            var fallback = extractors.OfType<PlainTextExtractor>().FirstOrDefault();
            return new ExtractorRegistry(extractors, fallback);
        });

        services.TryAddSingleton<FileIndexOptions>();
        services.TryAddSingleton<OutOfProcessExtractionOptions>();
        services.TryAddSingleton<IOutOfProcessExtractionService, OutOfProcessExtractionService>();
        services.TryAddSingleton<IIndexerRuntimeCondition, WindowsIndexerRuntimeCondition>();
        services.TryAddSingleton<IIndexVolumeResolver, WindowsIndexVolumeResolver>();
        services.TryAddSingleton<IUsnJournalReader, WindowsUsnJournalReader>();
        services.TryAddSingleton<IFileIndex>(sp => new CSharpDbFileIndex(
            sp.GetService<FileIndexOptions>(),
            sp.GetRequiredService<IFileWalker>(),
            sp.GetRequiredService<IExtractorRegistry>(),
            sp.GetService<SearchOptions>(),
            sp.GetService<ILogger<CSharpDbFileIndex>>(),
            sp.GetService<IIndexVolumeResolver>(),
            sp.GetService<IUsnJournalReader>(),
            sp.GetService<IOutOfProcessExtractionService>()));

        // Role-interface slices of the index, so consumers can depend on the
        // narrowest contract they need (ISP).
        services.TryAddSingleton<IIndexSearch>(sp => sp.GetRequiredService<IFileIndex>());
        services.TryAddSingleton<IIndexWriter>(sp => sp.GetRequiredService<IFileIndex>());
        services.TryAddSingleton<IIndexMaintenance>(sp => sp.GetRequiredService<IFileIndex>());
        services.TryAddSingleton<IPendingChangeStore>(sp => sp.GetRequiredService<IFileIndex>());
        services.TryAddSingleton<IIndexUsageStore>(sp =>
            sp.GetRequiredService<IFileIndex>() is IIndexUsageStore usageStore
                ? usageStore
                : NullIndexUsageStore.Instance);

        services.TryAddSingleton<IIndexCatchUpStore, CSharpDbIndexCatchUpStore>();
        services.TryAddSingleton<IIndexStartupCatchUpService, IndexStartupCatchUpService>();
        services.TryAddSingleton<IIndexQueue, IndexQueue>();
        services.TryAddSingleton<IIndexWatcherService, IndexWatcherService>();
        services.TryAddSingleton<IIndexingService, IndexingService>();
        services.TryAddSingleton<IIndexingSearchCoordinator>(sp =>
            new IndexingServiceSearchCoordinator(sp.GetRequiredService<IIndexingService>()));
        services.TryAddSingleton<IndexCoverageService>();

        services.TryAddSingleton(sp => new Searcher(
            sp.GetRequiredService<IFileWalker>(),
            sp.GetRequiredService<IExtractorRegistry>(),
            sp.GetService<SearchOptions>(),
            sp.GetService<ILogger<Searcher>>()));

        services.TryAddSingleton<ISearcher>(sp => new IndexedSearcher(
            sp.GetRequiredService<Searcher>(),
            sp.GetRequiredService<IFileIndex>(),
            sp.GetRequiredService<IndexCoverageService>(),
            sp.GetService<IIndexingSearchCoordinator>()));

        services.TryAddSingleton<IWorkflowStore>(_ => new JsonWorkflowStore());
        services.TryAddSingleton<IWorkflowRunner>(sp => new WorkflowRunner(
            sp.GetRequiredService<ISearcher>(),
            sp.GetRequiredService<IQueryFactory>(),
            sp.GetRequiredService<IExtractorRegistry>(),
            sp.GetService<SearchOptions>(),
            sp.GetService<ILogger<WorkflowRunner>>(),
            sp.GetService<ILoggerFactory>()));

        return services;
    }
}
