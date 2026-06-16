using System;
using System.Collections.Generic;
using System.Globalization;
using System.Runtime.CompilerServices;
using System.Text;

namespace FileSearch.Core.Indexing;

/// <summary>
/// Compiler-enforced safe SQL composition for the index database.
/// <para>
/// CSharpDB has no parameter-binding surface (every Execute overload takes
/// raw SQL text or a pre-built AST with no value dictionary), so values must
/// travel through the SQL text. <see cref="Sql.Format"/> makes that safe by
/// construction: every interpolated hole is rendered by a typed
/// <c>AppendFormatted</c> overload — strings are quoted with embedded quotes
/// doubled (null becomes NULL), numbers render invariant, identifiers and id
/// lists go through dedicated wrapper types — and there is deliberately no
/// catch-all overload, so interpolating anything else is a compile error.
/// A forgotten escape is impossible because there is no escape to remember.
/// </para>
/// </summary>
internal static class Sql
{
    public static string Format(Handler handler) => handler.ToString();

    /// <summary>
    /// A trusted SQL identifier (table name). Rejects anything but ASCII
    /// letters, digits, and underscores so an identifier hole can never
    /// smuggle SQL.
    /// </summary>
    public readonly struct Identifier
    {
        public Identifier(string name)
        {
            if (string.IsNullOrEmpty(name))
                throw new ArgumentException("SQL identifier must not be empty.", nameof(name));

            foreach (var ch in name)
            {
                if (!char.IsAsciiLetterOrDigit(ch) && ch != '_')
                    throw new ArgumentException($"Invalid SQL identifier: '{name}'.", nameof(name));
            }

            Name = name;
        }

        public string Name { get; }
    }

    /// <summary>A comma-joined list of integer ids for IN (...) clauses.</summary>
    public readonly struct IdList
    {
        private readonly IEnumerable<long> _ids;

        public IdList(IEnumerable<long> ids) => _ids = ids;

        internal void AppendTo(StringBuilder builder)
        {
            var first = true;
            foreach (var id in _ids)
            {
                if (!first)
                    builder.Append(',');
                builder.Append(id.ToString(CultureInfo.InvariantCulture));
                first = false;
            }

            // An empty list would render "IN ()", which is invalid SQL;
            // "IN (NULL)" is valid and matches nothing.
            if (first)
                builder.Append("NULL");
        }
    }

    [InterpolatedStringHandler]
    public ref struct Handler
    {
        private readonly StringBuilder _builder;

        public Handler(int literalLength, int formattedCount)
        {
            _builder = new StringBuilder(literalLength + formattedCount * 24);
        }

        public void AppendLiteral(string value) => _builder.Append(value);

        /// <summary>Strings are always values: quoted, embedded quotes doubled; null renders as NULL.</summary>
        public void AppendFormatted(string? value)
        {
            if (value is null)
            {
                _builder.Append("NULL");
                return;
            }

            _builder.Append('\'');
            _builder.Append(value.Replace("'", "''", StringComparison.Ordinal));
            _builder.Append('\'');
        }

        public void AppendFormatted(long value) => _builder.Append(value.ToString(CultureInfo.InvariantCulture));

        public void AppendFormatted(long? value)
        {
            if (value is { } number)
                AppendFormatted(number);
            else
                _builder.Append("NULL");
        }

        public void AppendFormatted(int value) => _builder.Append(value.ToString(CultureInfo.InvariantCulture));

        public void AppendFormatted(Identifier identifier) => _builder.Append(identifier.Name);

        public void AppendFormatted(IdList ids) => ids.AppendTo(_builder);

        public override string ToString() => _builder.ToString();
    }
}
