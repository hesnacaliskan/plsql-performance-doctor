using PlsqlDoctor.Core.Lexing;

namespace PlsqlDoctor.Core.PlSql;

/// <summary>
/// Token stream plus structural facts the rules need: how deeply each token is nested in loops
/// and on which line the innermost loop starts.
/// </summary>
public sealed class SqlContext
{
    private static readonly HashSet<string> BlockBoundaryWords = new(StringComparer.OrdinalIgnoreCase)
    {
        "BEGIN", "THEN", "ELSE", "LOOP", "IS", "AS", "DECLARE", "EXCEPTION",
    };

    private static readonly HashSet<string> StatementBoundaryWords = new(StringComparer.OrdinalIgnoreCase)
    {
        "BEGIN", "THEN", "ELSE", "LOOP", "DECLARE", "EXCEPTION",
    };

    public SqlContext(string source, string sourceName)
    {
        SourceName = sourceName;
        Lines = source.Replace("\r\n", "\n").Split('\n');
        Tokens = Lexer.Tokenize(source);
        LoopDepth = new int[Tokens.Count];
        LoopStartLine = new int[Tokens.Count];
        ComputeLoops();
    }

    public string SourceName { get; }
    public string[] Lines { get; }
    public IReadOnlyList<Token> Tokens { get; }

    /// <summary>Number of enclosing LOOP ... END LOOP blocks for each token.</summary>
    public int[] LoopDepth { get; }

    /// <summary>Line where the innermost enclosing loop starts (0 when not in a loop).</summary>
    public int[] LoopStartLine { get; }

    public Token? At(int index) => index >= 0 && index < Tokens.Count ? Tokens[index] : null;

    public bool IsWordAt(int index, string word) => At(index)?.IsWord(word) == true;

    public bool IsSymbolAt(int index, string symbol) => At(index)?.IsSymbol(symbol) == true;

    public string LineText(int line) => line >= 1 && line <= Lines.Length ? Lines[line - 1].Trim() : "";

    /// <summary>Index of the ';' that ends the statement containing <paramref name="start"/>, or Tokens.Count.</summary>
    public int StatementEnd(int start)
    {
        for (var j = start; j < Tokens.Count; j++)
        {
            if (Tokens[j].IsSymbol(";")) return j;
        }

        return Tokens.Count;
    }

    /// <summary>Index of the first token of the statement containing <paramref name="index"/>.</summary>
    public int StatementStart(int index)
    {
        var depth = 0;
        for (var j = index - 1; j >= 0; j--)
        {
            var t = Tokens[j];
            if (t.IsSymbol(")"))
            {
                depth++;
            }
            else if (t.IsSymbol("("))
            {
                if (depth == 0) return j + 1;
                depth--;
            }
            else if (depth == 0 && (t.IsSymbol(";") || t.IsSymbol(">>") || t.IsWordIn(StatementBoundaryWords)))
            {
                return j + 1;
            }
        }

        return 0;
    }

    /// <summary>Index of the ')' matching the '(' at <paramref name="open"/>, or -1.</summary>
    public int MatchingParen(int open)
    {
        var depth = 0;
        for (var j = open; j < Tokens.Count; j++)
        {
            var t = Tokens[j];
            if (t.IsSymbol("("))
            {
                depth++;
            }
            else if (t.IsSymbol(")"))
            {
                if (--depth == 0) return j;
            }
            else if (t.IsSymbol(";"))
            {
                return -1;
            }
        }

        return -1;
    }

    /// <summary>
    /// Scans forward at the same parenthesis level until the end of the statement.
    /// Returns the index of the first token matching <paramref name="match"/>, or -1 if
    /// <paramref name="stop"/> matches first or the statement ends.
    /// </summary>
    public int FindTopLevel(int start, Func<Token, bool> match, Func<Token, bool>? stop = null)
    {
        var depth = 0;
        for (var j = start; j < Tokens.Count; j++)
        {
            var t = Tokens[j];
            if (t.IsSymbol("("))
            {
                depth++;
                continue;
            }

            if (t.IsSymbol(")"))
            {
                if (--depth < 0) return -1;
                continue;
            }

            if (depth > 0) continue;
            if (t.IsSymbol(";")) return -1;
            if (match(t)) return j;
            if (stop?.Invoke(t) == true) return -1;
        }

        return -1;
    }

    private void ComputeLoops()
    {
        var open = new Stack<int>();
        for (var i = 0; i < Tokens.Count; i++)
        {
            var t = Tokens[i];
            if (t.IsWord("END") && IsWordAt(i + 1, "LOOP"))
            {
                if (open.Count > 0) open.Pop();
                Mark(i, open);
                Mark(i + 1, open);
                i++;
                continue;
            }

            Mark(i, open);
            if (t.IsWord("LOOP")) open.Push(LoopOpenerLine(i));
        }
    }

    private void Mark(int i, Stack<int> open)
    {
        LoopDepth[i] = open.Count;
        LoopStartLine[i] = open.Count > 0 ? open.Peek() : 0;
    }

    // For "FOR r IN (...) LOOP" or "WHILE x LOOP" return the line of FOR/WHILE, else the LOOP line.
    private int LoopOpenerLine(int loopIndex)
    {
        var depth = 0;
        var j = loopIndex - 1;
        for (; j >= 0; j--)
        {
            var t = Tokens[j];
            if (t.IsSymbol(")"))
            {
                depth++;
                continue;
            }

            if (t.IsSymbol("("))
            {
                if (depth == 0) break;
                depth--;
                continue;
            }

            if (depth == 0 && (t.IsSymbol(";") || t.IsSymbol(">>") || t.IsWordIn(BlockBoundaryWords))) break;
        }

        var first = At(j + 1);
        return first is not null && j + 1 < loopIndex && (first.IsWord("FOR") || first.IsWord("WHILE"))
            ? first.Line
            : Tokens[loopIndex].Line;
    }
}
