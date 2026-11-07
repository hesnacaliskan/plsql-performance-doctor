using System.Text;

namespace PlsqlDoctor.Core.Lexing;

/// <summary>
/// A small PL/SQL lexer. It drops comments and turns string literals into single tokens,
/// so rules never match keywords that only appear inside comments or strings.
/// </summary>
public sealed class Lexer
{
    private static readonly string[] TwoCharSymbols =
        [":=", "||", "<=", ">=", "<>", "!=", "^=", "=>", "..", "**", "<<", ">>"];

    private readonly string _src;
    private int _pos;
    private int _line = 1;
    private int _col = 1;

    private Lexer(string source) => _src = source;

    public static IReadOnlyList<Token> Tokenize(string source) => new Lexer(source).Run();

    private char Peek(int offset = 0) => _pos + offset < _src.Length ? _src[_pos + offset] : '\0';

    private bool AtEnd => _pos >= _src.Length;

    private void Advance(int count = 1)
    {
        for (var k = 0; k < count && !AtEnd; k++)
        {
            if (_src[_pos] == '\n')
            {
                _line++;
                _col = 1;
            }
            else
            {
                _col++;
            }

            _pos++;
        }
    }

    private List<Token> Run()
    {
        var tokens = new List<Token>();
        while (!AtEnd)
        {
            var c = Peek();
            if (char.IsWhiteSpace(c))
            {
                Advance();
                continue;
            }

            if (c == '-' && Peek(1) == '-')
            {
                while (!AtEnd && Peek() != '\n') Advance();
                continue;
            }

            if (c == '/' && Peek(1) == '*')
            {
                Advance(2);
                while (!AtEnd && !(Peek() == '*' && Peek(1) == '/')) Advance();
                Advance(2);
                continue;
            }

            int line = _line, col = _col;

            if (c is 'q' or 'Q' && Peek(1) == '\'')
            {
                Advance();
                tokens.Add(new Token(TokenKind.String, ReadQQuote(), line, col));
                continue;
            }

            if (c is 'n' or 'N' && Peek(1) is 'q' or 'Q' && Peek(2) == '\'')
            {
                Advance(2);
                tokens.Add(new Token(TokenKind.String, ReadQQuote(), line, col));
                continue;
            }

            if (c is 'n' or 'N' && Peek(1) == '\'')
            {
                Advance();
                tokens.Add(new Token(TokenKind.String, ReadString(), line, col));
                continue;
            }

            if (c == '\'')
            {
                tokens.Add(new Token(TokenKind.String, ReadString(), line, col));
                continue;
            }

            if (c == '"')
            {
                tokens.Add(new Token(TokenKind.QuotedIdentifier, ReadQuotedIdentifier(), line, col));
                continue;
            }

            if (char.IsDigit(c) || (c == '.' && char.IsDigit(Peek(1))))
            {
                tokens.Add(new Token(TokenKind.Number, ReadNumber(), line, col));
                continue;
            }

            if (char.IsLetter(c))
            {
                tokens.Add(new Token(TokenKind.Word, ReadWord(), line, col));
                continue;
            }

            if (c == ':' && (char.IsLetterOrDigit(Peek(1)) || Peek(1) == '_'))
            {
                Advance();
                tokens.Add(new Token(TokenKind.BindVariable, ":" + ReadWord(), line, col));
                continue;
            }

            var two = _pos + 1 < _src.Length ? _src.Substring(_pos, 2) : null;
            if (two is not null && TwoCharSymbols.Contains(two))
            {
                Advance(2);
                tokens.Add(new Token(TokenKind.Symbol, two, line, col));
                continue;
            }

            Advance();
            tokens.Add(new Token(TokenKind.Symbol, c.ToString(), line, col));
        }

        return tokens;
    }

    // Positioned on the opening quote of q'Xtext X'.
    private string ReadQQuote()
    {
        Advance(); // the quote
        var open = Peek();
        Advance();
        var close = open switch
        {
            '[' => ']',
            '{' => '}',
            '(' => ')',
            '<' => '>',
            _ => open,
        };

        var sb = new StringBuilder();
        while (!AtEnd && !(Peek() == close && Peek(1) == '\''))
        {
            sb.Append(Peek());
            Advance();
        }

        Advance(2);
        return sb.ToString();
    }

    private string ReadString()
    {
        Advance(); // opening quote
        var sb = new StringBuilder();
        while (!AtEnd)
        {
            var c = Peek();
            if (c == '\'')
            {
                if (Peek(1) == '\'')
                {
                    sb.Append('\'');
                    Advance(2);
                    continue;
                }

                Advance();
                break;
            }

            sb.Append(c);
            Advance();
        }

        return sb.ToString();
    }

    private string ReadQuotedIdentifier()
    {
        Advance();
        var sb = new StringBuilder();
        while (!AtEnd && Peek() != '"')
        {
            sb.Append(Peek());
            Advance();
        }

        Advance();
        return sb.ToString();
    }

    private string ReadNumber()
    {
        var start = _pos;
        while (char.IsDigit(Peek())) Advance();

        // "1..10" is a range, not a decimal point.
        if (Peek() == '.' && Peek(1) != '.')
        {
            Advance();
            while (char.IsDigit(Peek())) Advance();
        }

        if (Peek() is 'e' or 'E' && (char.IsDigit(Peek(1)) || (Peek(1) is '+' or '-' && char.IsDigit(Peek(2)))))
        {
            Advance(2);
            while (char.IsDigit(Peek())) Advance();
        }

        if (Peek() is 'f' or 'F' or 'd' or 'D' && !char.IsLetterOrDigit(Peek(1))) Advance();
        return _src[start.._pos];
    }

    private string ReadWord()
    {
        var start = _pos;
        while (char.IsLetterOrDigit(Peek()) || Peek() is '_' or '$' or '#') Advance();
        return _src[start.._pos];
    }
}
