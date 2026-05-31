namespace FileSearch.Core.Extractors;

/// <summary>
/// A single line of text yielded by an <see cref="ITextExtractor"/>.
/// </summary>
public readonly record struct TextLine(int Number, string Content);
