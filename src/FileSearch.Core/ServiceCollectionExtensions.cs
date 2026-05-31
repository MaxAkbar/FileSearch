using System.Linq;
using FileSearch.Core.Engine;
using FileSearch.Core.Extractors;
using FileSearch.Core.Queries;
using FileSearch.Core.Walker;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

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

        services.TryAddSingleton<ISearcher>(sp => new Searcher(
            sp.GetRequiredService<IFileWalker>(),
            sp.GetRequiredService<IExtractorRegistry>(),
            sp.GetService<SearchOptions>()));

        return services;
    }
}
