using System.Collections.Generic;

namespace FileSearch.Gui.Settings;

public sealed class FileTypeOptions
{
    public List<string> DocumentExtensions { get; set; } = new()
    {
        ".pdf", ".docx", ".xlsx", ".pptx", ".rtf", ".odt", ".ods", ".odp", ".epub", ".eml",
    };

    public List<string> AdditionalPlainTextExtensions { get; set; } = new();
}
