using System.Collections.Generic;
using FileSearch.Core.Queries;
using FileSearch.Core.Walker;

namespace FileSearch.Core.Engine;

public sealed record SearchRequest(
    Query Expression,
    IReadOnlyList<string> Roots,
    WalkerOptions WalkerOptions);
