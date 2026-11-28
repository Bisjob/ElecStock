namespace ElecStock.Kicad;

/// <summary>One list inside the root list of a KiCad file: <c>(symbol "Name" …)</c>, as a span of the text.</summary>
/// <param name="Start">Index of the opening parenthesis.</param>
/// <param name="End">Index just after the closing parenthesis.</param>
/// <param name="Head">First atom: <c>symbol</c>, <c>model</c>, <c>version</c>…</param>
/// <param name="Arg">Second token (string unquoted), or null when the list has none.</param>
/// <param name="ArgStart">Index of the second token in the text (its opening quote for a string).</param>
/// <param name="ArgEnd">Index just after the second token.</param>
public readonly record struct SexprNode(int Start, int End, string Head, string? Arg, int ArgStart, int ArgEnd);

/// <summary>
/// Just enough of the KiCad S-expression format to find the top-level lists of a file and slice them
/// out verbatim: the content is never rewritten, only cut and joined.
/// </summary>
public static class Sexpr
{
    /// <summary>The root list: its head (<c>kicad_symbol_lib</c>, <c>footprint</c>…), its first argument and its child lists.</summary>
    public static (string Head, string? Arg, List<SexprNode> Children) Root(string text)
    {
        int i = SkipSpace(text, 0);
        if (i >= text.Length || text[i] != '(') throw new FormatException("Not a KiCad file: it does not start with '('.");
        var root = ReadList(text, i);
        var children = new List<SexprNode>();
        int pos = root.ArgEnd > 0 ? root.ArgEnd : HeadEnd(text, i);
        while (true)
        {
            pos = SkipSpace(text, pos);
            if (pos >= text.Length) throw new FormatException("Unbalanced parentheses.");
            var c = text[pos];
            if (c == ')') break;
            if (c == '(')
            {
                var child = ReadList(text, pos);
                children.Add(child);
                pos = child.End;
            }
            else pos = SkipToken(text, pos);
        }
        return (root.Head, root.Arg, children);
    }

    /// <summary>Child lists of <paramref name="node"/>.</summary>
    public static List<SexprNode> Children(string text, SexprNode node)
    {
        var children = new List<SexprNode>();
        int pos = node.ArgEnd > 0 ? node.ArgEnd : HeadEnd(text, node.Start);
        while (true)
        {
            pos = SkipSpace(text, pos);
            if (pos >= node.End - 1) break;
            if (text[pos] == '(')
            {
                var child = ReadList(text, pos);
                children.Add(child);
                pos = child.End;
            }
            else pos = SkipToken(text, pos);
        }
        return children;
    }

    /// <summary>A KiCad string token: <c>"a \"b\""</c>.</summary>
    public static string Quote(string value) => "\"" + value.Replace("\\", "\\\\").Replace("\"", "\\\"") + "\"";

    static SexprNode ReadList(string text, int start)
    {
        int end = MatchParen(text, start);
        int pos = SkipSpace(text, start + 1);
        int headStart = pos;
        pos = SkipToken(text, pos);
        var head = text[headStart..pos];
        pos = SkipSpace(text, pos);
        if (pos >= end - 1 || text[pos] == '(' || text[pos] == ')') return new SexprNode(start, end, head, null, 0, 0);
        int argStart = pos;
        int argEnd = SkipToken(text, pos);
        return new SexprNode(start, end, head, Unquote(text[argStart..argEnd]), argStart, argEnd);
    }

    static int HeadEnd(string text, int start) => SkipToken(text, SkipSpace(text, start + 1));

    static int MatchParen(string text, int start)
    {
        int depth = 0;
        for (int i = start; i < text.Length; i++)
        {
            var c = text[i];
            if (c == '"')
            {
                i = StringEnd(text, i) - 1;
                continue;
            }
            if (c == '(') depth++;
            else if (c == ')' && --depth == 0) return i + 1;
        }
        throw new FormatException("Unbalanced parentheses.");
    }

    /// <summary>Index just after the closing quote of the string starting at <paramref name="start"/>.</summary>
    static int StringEnd(string text, int start)
    {
        for (int i = start + 1; i < text.Length; i++)
        {
            if (text[i] == '\\') i++;
            else if (text[i] == '"') return i + 1;
        }
        throw new FormatException("Unterminated string.");
    }

    static int SkipToken(string text, int pos)
    {
        if (pos < text.Length && text[pos] == '"') return StringEnd(text, pos);
        while (pos < text.Length && !char.IsWhiteSpace(text[pos]) && text[pos] != '(' && text[pos] != ')') pos++;
        return pos;
    }

    static int SkipSpace(string text, int pos)
    {
        while (pos < text.Length && char.IsWhiteSpace(text[pos])) pos++;
        return pos;
    }

    static string Unquote(string token)
    {
        if (token.Length < 2 || token[0] != '"') return token;
        var inner = token[1..^1];
        if (!inner.Contains('\\')) return inner;
        var sb = new System.Text.StringBuilder(inner.Length);
        for (int i = 0; i < inner.Length; i++)
        {
            if (inner[i] == '\\' && i + 1 < inner.Length)
            {
                i++;
                sb.Append(inner[i] switch { 'n' => '\n', 't' => '\t', _ => inner[i] });
            }
            else sb.Append(inner[i]);
        }
        return sb.ToString();
    }
}
