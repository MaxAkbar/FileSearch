namespace FileSearch.Core.Indexing;

public enum IndexChangeKind
{
    RefreshRoot,
    UpsertFile,
    DeleteFile,
    RefreshSemanticRoot,
}
