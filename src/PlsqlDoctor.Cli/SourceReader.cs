using System.Globalization;
using System.Text;

namespace PlsqlDoctor.Cli;

/// <summary>
/// Reads source files in the encoding they were written in. Old PL/SQL code bases are often saved
/// in a Windows ANSI code page (for example windows-1254 for Turkish), not in UTF-8.
/// </summary>
internal static class SourceReader
{
    /// <summary>Marker returned by <see cref="ParseEncoding"/> for "auto".</summary>
    public static readonly Encoding Auto = new UTF8Encoding(false, true);

    private static readonly UTF8Encoding StrictUtf8 = new(false, true);

    static SourceReader() => Encoding.RegisterProvider(CodePagesEncodingProvider.Instance);

    public static string Read(string path, Encoding? encoding) => Decode(File.ReadAllBytes(path), encoding);

    /// <summary>
    /// With an explicit encoding, uses it. Otherwise honours a byte order mark, then tries strict UTF-8,
    /// and falls back to the ANSI code page of the current culture.
    /// </summary>
    public static string Decode(byte[] bytes, Encoding? encoding)
    {
        using var stream = new MemoryStream(bytes);
        if (encoding is not null)
        {
            using var forced = new StreamReader(stream, encoding, detectEncodingFromByteOrderMarks: true);
            return forced.ReadToEnd();
        }

        if (HasBom(bytes))
        {
            using var bom = new StreamReader(stream, StrictUtf8, detectEncodingFromByteOrderMarks: true);
            return bom.ReadToEnd();
        }

        try
        {
            return StrictUtf8.GetString(bytes);
        }
        catch (DecoderFallbackException)
        {
            return Encoding.GetEncoding(CultureInfo.CurrentCulture.TextInfo.ANSICodePage).GetString(bytes);
        }
    }

    public static Encoding? ParseEncoding(string value)
    {
        if (value.Equals("auto", StringComparison.OrdinalIgnoreCase)) return Auto;

        try
        {
            return int.TryParse(value, NumberStyles.Integer, CultureInfo.InvariantCulture, out var codePage)
                ? Encoding.GetEncoding(codePage)
                : Encoding.GetEncoding(value);
        }
        catch (Exception ex) when (ex is ArgumentException or NotSupportedException)
        {
            return null;
        }
    }

    private static bool HasBom(byte[] b) =>
        (b.Length >= 3 && b[0] == 0xEF && b[1] == 0xBB && b[2] == 0xBF) ||
        (b.Length >= 2 && ((b[0] == 0xFF && b[1] == 0xFE) || (b[0] == 0xFE && b[1] == 0xFF)));
}
