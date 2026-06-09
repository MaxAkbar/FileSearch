using System.Text;

namespace FileSearch.Cli;

internal static class CommandLine
{
    public static IReadOnlyList<string> Tokenize(string input)
    {
        var tokens = new List<string>();
        var current = new StringBuilder();
        char? quote = null;

        for (var i = 0; i < input.Length; i++)
        {
            var c = input[i];
            if (quote is not null)
            {
                if (c == quote.Value)
                {
                    quote = null;
                    continue;
                }

                if (c == '\\' && i + 1 < input.Length &&
                    (input[i + 1] == quote.Value || input[i + 1] == '\\'))
                {
                    current.Append(input[++i]);
                    continue;
                }

                current.Append(c);
                continue;
            }

            if (c is '"' or '\'')
            {
                quote = c;
                continue;
            }

            if (char.IsWhiteSpace(c))
            {
                Flush();
                continue;
            }

            current.Append(c);
        }

        Flush();
        return tokens;

        void Flush()
        {
            if (current.Length == 0)
                return;

            tokens.Add(current.ToString());
            current.Clear();
        }
    }

    public static string JoinArgs(IEnumerable<string> args) =>
        string.Join(' ', args);
}
