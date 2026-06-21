using System;

namespace FileSearch.Core.Engine;

[Flags]
public enum RetrievalLayer
{
    None = 0,
    Instant = 1 << 0,
    Deep = 1 << 1,
    Smart = 1 << 2,
}

