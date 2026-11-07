namespace PlsqlDoctor.Core.Lexing;

public enum TokenKind
{
    Word,
    QuotedIdentifier,
    Number,
    String,
    BindVariable,
    Symbol,
}

/// <summary>A lexical token. For strings and quoted identifiers <see cref="Text"/> holds the unquoted value.</summary>
public sealed record Token(TokenKind Kind, string Text, int Line, int Column)
{
    public bool IsWord(string word) =>
        Kind == TokenKind.Word && Text.Equals(word, StringComparison.OrdinalIgnoreCase);

    public bool IsWordIn(IReadOnlySet<string> words) => Kind == TokenKind.Word && words.Contains(Text);

    public bool IsSymbol(string symbol) => Kind == TokenKind.Symbol && Text == symbol;

    public bool IsIdentifier => Kind is TokenKind.Word or TokenKind.QuotedIdentifier;
}
