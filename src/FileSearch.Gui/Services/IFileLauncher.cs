namespace FileSearch.Gui.Services;

/// <summary>
/// OS-level actions on a file path (open in default handler, reveal in
/// Explorer, copy to clipboard). Abstracted behind an interface so view
/// models stay testable.
/// </summary>
public interface IFileLauncher
{
    void Open(string path);
    void RevealInExplorer(string path);
    void CopyToClipboard(string text);
}
