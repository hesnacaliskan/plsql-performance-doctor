using PlsqlDoctor.Core.Lexing;

namespace PlsqlDoctor.Tests;

public class LexerTests
{
    [Fact]
    public void Comments_are_skipped_and_strings_are_single_tokens()
    {
        var tokens = Lexer.Tokenize("SELECT 'a -- not a comment' FROM dual -- COMMIT\n/* LOOP\n */ WHERE");

        Assert.Equal(["SELECT", "a -- not a comment", "FROM", "dual", "WHERE"], tokens.Select(t => t.Text));
        Assert.Equal(TokenKind.String, tokens[1].Kind);
        Assert.Equal(3, tokens[^1].Line);
    }

    [Fact]
    public void Quote_escapes_and_q_quotes_are_supported()
    {
        var tokens = Lexer.Tokenize("q'[it's]' 'it''s' N'x'");

        Assert.All(tokens, t => Assert.Equal(TokenKind.String, t.Kind));
        Assert.Equal(["it's", "it's", "x"], tokens.Select(t => t.Text));
    }

    [Fact]
    public void Range_operator_is_not_a_decimal_point()
    {
        var tokens = Lexer.Tokenize("FOR i IN 1..10 LOOP");

        Assert.Equal(["FOR", "i", "IN", "1", "..", "10", "LOOP"], tokens.Select(t => t.Text));
    }

    [Fact]
    public void Bind_variables_and_assignment_are_recognized()
    {
        var tokens = Lexer.Tokenize("l_x := :id || :2;");

        Assert.Equal(TokenKind.Symbol, tokens[1].Kind);
        Assert.Equal(":=", tokens[1].Text);
        Assert.Equal(TokenKind.BindVariable, tokens[2].Kind);
        Assert.Equal("||", tokens[3].Text);
        Assert.Equal(":2", tokens[4].Text);
    }
}
