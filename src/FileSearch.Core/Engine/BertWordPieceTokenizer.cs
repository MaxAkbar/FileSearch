using System.Globalization;
using System.Text;

namespace FileSearch.Core.Engine;

internal sealed class BertWordPieceTokenizer
{
    private readonly IReadOnlyDictionary<string, int> _vocabulary;
    private readonly int _unknownTokenId;
    private readonly int _clsTokenId;
    private readonly int _sepTokenId;
    private readonly bool _doLowerCase;

    private BertWordPieceTokenizer(IReadOnlyDictionary<string, int> vocabulary, bool doLowerCase)
    {
        _vocabulary = vocabulary;
        _doLowerCase = doLowerCase;
        _unknownTokenId = GetRequiredToken("[UNK]");
        _clsTokenId = GetRequiredToken("[CLS]");
        _sepTokenId = GetRequiredToken("[SEP]");
    }

    public static async Task<BertWordPieceTokenizer> LoadAsync(
        string vocabularyPath,
        bool doLowerCase,
        CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(vocabularyPath))
            throw new ArgumentException("Vocabulary path is required.", nameof(vocabularyPath));

        var vocabulary = new Dictionary<string, int>(StringComparer.Ordinal);
        var index = 0;
        foreach (var line in await File.ReadAllLinesAsync(vocabularyPath, cancellationToken).ConfigureAwait(false))
        {
            var token = line.Trim();
            if (token.Length == 0)
                continue;

            vocabulary[token] = index++;
        }

        return new BertWordPieceTokenizer(vocabulary, doLowerCase);
    }

    public TokenizedText Encode(string text, int maxTokens)
    {
        if (maxTokens < 3)
            throw new ArgumentOutOfRangeException(nameof(maxTokens), "At least three tokens are required.");

        var ids = new List<long>(Math.Min(maxTokens, 128)) { _clsTokenId };
        foreach (var token in BasicTokenize(text ?? string.Empty))
        {
            foreach (var wordPiece in WordPieceTokenize(token))
            {
                if (ids.Count >= maxTokens - 1)
                    break;

                ids.Add(wordPiece);
            }

            if (ids.Count >= maxTokens - 1)
                break;
        }

        ids.Add(_sepTokenId);
        return new TokenizedText(
            ids.ToArray(),
            Enumerable.Repeat(1L, ids.Count).ToArray(),
            new long[ids.Count]);
    }

    private IEnumerable<string> BasicTokenize(string text)
    {
        var token = new StringBuilder();
        foreach (var ch in text)
        {
            if (char.IsWhiteSpace(ch) || char.IsControl(ch))
            {
                if (token.Length > 0)
                {
                    yield return NormalizeToken(token.ToString());
                    token.Clear();
                }

                continue;
            }

            if (IsPunctuation(ch))
            {
                if (token.Length > 0)
                {
                    yield return NormalizeToken(token.ToString());
                    token.Clear();
                }

                yield return NormalizeToken(ch.ToString());
                continue;
            }

            token.Append(ch);
        }

        foreach (var finalToken in FlushToken(token))
            yield return finalToken;

        IEnumerable<string> FlushToken(StringBuilder builder)
        {
            if (builder.Length == 0)
                yield break;

            yield return NormalizeToken(builder.ToString());
            builder.Clear();
        }
    }

    private IEnumerable<long> WordPieceTokenize(string token)
    {
        if (token.Length == 0)
            yield break;

        var start = 0;
        var pieces = new List<long>();
        while (start < token.Length)
        {
            var end = token.Length;
            int? currentId = null;
            while (start < end)
            {
                var candidate = start == 0
                    ? token[start..end]
                    : "##" + token[start..end];
                if (_vocabulary.TryGetValue(candidate, out var id))
                {
                    currentId = id;
                    break;
                }

                end--;
            }

            if (currentId is null)
            {
                yield return _unknownTokenId;
                yield break;
            }

            pieces.Add(currentId.Value);
            start = end;
        }

        foreach (var piece in pieces)
            yield return piece;
    }

    private string NormalizeToken(string token)
    {
        var value = _doLowerCase ? token.ToLowerInvariant() : token;
        if (!_doLowerCase)
            return value;

        var normalized = value.Normalize(NormalizationForm.FormD);
        var builder = new StringBuilder(normalized.Length);
        foreach (var ch in normalized)
        {
            if (CharUnicodeInfo.GetUnicodeCategory(ch) != UnicodeCategory.NonSpacingMark)
                builder.Append(ch);
        }

        return builder.ToString().Normalize(NormalizationForm.FormC);
    }

    private int GetRequiredToken(string token) =>
        _vocabulary.TryGetValue(token, out var id)
            ? id
            : throw new InvalidOperationException($"Vocabulary is missing required token {token}.");

    private static bool IsPunctuation(char ch)
    {
        var category = CharUnicodeInfo.GetUnicodeCategory(ch);
        return category is UnicodeCategory.ConnectorPunctuation
            or UnicodeCategory.DashPunctuation
            or UnicodeCategory.OpenPunctuation
            or UnicodeCategory.ClosePunctuation
            or UnicodeCategory.InitialQuotePunctuation
            or UnicodeCategory.FinalQuotePunctuation
            or UnicodeCategory.OtherPunctuation
            or UnicodeCategory.MathSymbol
            or UnicodeCategory.CurrencySymbol
            or UnicodeCategory.ModifierSymbol
            or UnicodeCategory.OtherSymbol;
    }
}

internal sealed record TokenizedText(
    long[] InputIds,
    long[] AttentionMask,
    long[] TokenTypeIds);
