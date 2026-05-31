using System;
using System.Collections.Generic;
using System.Text;

namespace FileSearch.Core.Queries;

/// <summary>
/// Parses a search expression into a <see cref="Query"/> AST.
/// Grammar:
/// <code>
///   expr     := orExpr
///   orExpr   := andExpr ( "OR" andExpr )*
///   andExpr  := notExpr ( ("AND" | implicit) notExpr )*
///   notExpr  := "NOT"? primary
///   primary  := QUOTED | REGEX | WORD | "(" expr ")"
/// </code>
/// Tokens: bare words, "double-quoted", /regex/, parens, AND/OR/NOT (case-insensitive).
/// Adjacent primaries with no explicit operator are joined by AND.
/// </summary>
public sealed class QueryParser : IQueryParser
{
    private readonly bool _caseSensitive;

    public QueryParser(bool caseSensitive = false) =>
        _caseSensitive = caseSensitive;

    public Query Parse(string input)
    {
        if (string.IsNullOrWhiteSpace(input))
            throw new ArgumentException("Query is empty.", nameof(input));

        var tokens = Tokenize(input);
        var cursor = new TokenCursor(tokens);
        var query = ParseOr(cursor);

        if (!cursor.AtEnd)
            throw new FormatException($"Unexpected token '{cursor.Peek().Text}' at position {cursor.Peek().Position}.");

        return query;
    }

    // ---------- Parsing ----------

    private Query ParseOr(TokenCursor c)
    {
        var children = new List<Query> { ParseAnd(c) };
        while (c.MatchKeyword("OR"))
            children.Add(ParseAnd(c));
        return children.Count == 1 ? children[0] : new OrQuery(children);
    }

    private Query ParseAnd(TokenCursor c)
    {
        var children = new List<Query> { ParseNot(c) };
        while (true)
        {
            if (c.MatchKeyword("AND"))
            {
                children.Add(ParseNot(c));
                continue;
            }
            // Implicit AND: next token starts a new primary.
            if (StartsPrimary(c))
            {
                children.Add(ParseNot(c));
                continue;
            }
            break;
        }
        return children.Count == 1 ? children[0] : new AndQuery(children);
    }

    private Query ParseNot(TokenCursor c)
    {
        if (c.MatchKeyword("NOT"))
            return new NotQuery(ParseNot(c));
        return ParsePrimary(c);
    }

    private Query ParsePrimary(TokenCursor c)
    {
        if (c.AtEnd)
            throw new FormatException("Expected a term but reached end of input.");

        var tok = c.Peek();
        switch (tok.Kind)
        {
            case TokenKind.LParen:
                c.Advance();
                var inner = ParseOr(c);
                if (!c.MatchKind(TokenKind.RParen))
                    throw new FormatException("Missing closing parenthesis.");
                return inner;

            case TokenKind.Quoted:
                c.Advance();
                return new TermQuery(tok.Text, _caseSensitive);

            case TokenKind.Regex:
                c.Advance();
                return new RegexQuery(tok.Text, _caseSensitive);

            case TokenKind.Word:
                // Bare words shouldn't be reserved keywords here — those are handled above.
                c.Advance();
                return new TermQuery(tok.Text, _caseSensitive);

            default:
                throw new FormatException($"Unexpected token '{tok.Text}' at position {tok.Position}.");
        }
    }

    private static bool StartsPrimary(TokenCursor c)
    {
        if (c.AtEnd) return false;
        var tok = c.Peek();
        return tok.Kind switch
        {
            TokenKind.LParen => true,
            TokenKind.Quoted => true,
            TokenKind.Regex => true,
            TokenKind.Word => !IsKeyword(tok.Text),
            _ => false,
        };
    }

    private static bool IsKeyword(string text) =>
        text.Equals("AND", StringComparison.OrdinalIgnoreCase)
        || text.Equals("OR", StringComparison.OrdinalIgnoreCase)
        || text.Equals("NOT", StringComparison.OrdinalIgnoreCase);

    // ---------- Tokenization ----------

    internal enum TokenKind { Word, Quoted, Regex, LParen, RParen }

    internal readonly record struct Token(TokenKind Kind, string Text, int Position);

    internal static IReadOnlyList<Token> Tokenize(string input)
    {
        var tokens = new List<Token>();
        var sb = new StringBuilder();
        int i = 0;
        while (i < input.Length)
        {
            char c = input[i];
            if (char.IsWhiteSpace(c)) { i++; continue; }

            if (c == '(') { tokens.Add(new Token(TokenKind.LParen, "(", i)); i++; continue; }
            if (c == ')') { tokens.Add(new Token(TokenKind.RParen, ")", i)); i++; continue; }

            if (c == '"')
            {
                int start = i;
                i++;
                sb.Clear();
                while (i < input.Length && input[i] != '"')
                {
                    if (input[i] == '\\' && i + 1 < input.Length)
                    {
                        sb.Append(input[i + 1]);
                        i += 2;
                    }
                    else
                    {
                        sb.Append(input[i]);
                        i++;
                    }
                }
                if (i >= input.Length)
                    throw new FormatException($"Unterminated string starting at position {start}.");
                i++; // closing quote
                tokens.Add(new Token(TokenKind.Quoted, sb.ToString(), start));
                continue;
            }

            if (c == '/')
            {
                int start = i;
                i++;
                sb.Clear();
                while (i < input.Length && input[i] != '/')
                {
                    if (input[i] == '\\' && i + 1 < input.Length && input[i + 1] == '/')
                    {
                        sb.Append('/');
                        i += 2;
                    }
                    else
                    {
                        sb.Append(input[i]);
                        i++;
                    }
                }
                if (i >= input.Length)
                    throw new FormatException($"Unterminated regex starting at position {start}.");
                i++; // closing slash
                tokens.Add(new Token(TokenKind.Regex, sb.ToString(), start));
                continue;
            }

            // Bare word: until whitespace or special char.
            int wordStart = i;
            sb.Clear();
            while (i < input.Length && !char.IsWhiteSpace(input[i]) && input[i] != '(' && input[i] != ')')
            {
                sb.Append(input[i]);
                i++;
            }
            tokens.Add(new Token(TokenKind.Word, sb.ToString(), wordStart));
        }
        return tokens;
    }

    internal sealed class TokenCursor
    {
        private readonly IReadOnlyList<Token> _tokens;
        private int _index;

        public TokenCursor(IReadOnlyList<Token> tokens) => _tokens = tokens;

        public bool AtEnd => _index >= _tokens.Count;
        public Token Peek() => _tokens[_index];
        public void Advance() => _index++;

        public bool MatchKind(TokenKind kind)
        {
            if (AtEnd || _tokens[_index].Kind != kind) return false;
            _index++;
            return true;
        }

        public bool MatchKeyword(string keyword)
        {
            if (AtEnd) return false;
            var t = _tokens[_index];
            if (t.Kind == TokenKind.Word && t.Text.Equals(keyword, StringComparison.OrdinalIgnoreCase))
            {
                _index++;
                return true;
            }
            return false;
        }
    }
}
