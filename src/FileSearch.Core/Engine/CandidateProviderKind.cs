using System;

namespace FileSearch.Core.Engine;

[Flags]
public enum CandidateProviderKind
{
    None = 0,
    Metadata = 1 << 0,
    Lexical = 1 << 1,
    Regex = 1 << 2,
    Fuzzy = 1 << 3,
    Semantic = 1 << 4,
    Ocr = 1 << 5,
    History = 1 << 6,
    Remote = 1 << 7,
}

