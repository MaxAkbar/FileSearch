using FileSearch.Core.Engine;

namespace FileSearch.Core.Indexing;

internal static class IndexedSearchRequestSupport
{
    public static bool CanUseIndex(SearchRequest request, out string fallbackReason)
    {
        fallbackReason = string.Empty;
        if (request.SearchTarget == SearchTarget.Content)
            return true;

        if (request.SearchTarget is SearchTarget.FolderNames or SearchTarget.FileAndFolderNames)
        {
            fallbackReason = "Folder name search is not indexed yet";
            return false;
        }

        if (!MetadataSearchSpec.TryCreate(request, out _))
        {
            fallbackReason = "This name query is not supported by the metadata index";
            return false;
        }

        return true;
    }

    public static void ThrowIfUnsupported(SearchRequest request)
    {
        if (CanUseIndex(request, out var reason))
            return;

        throw new NotSupportedException($"{reason}. Route the request through IndexedSearcher to use the live fallback.");
    }
}
