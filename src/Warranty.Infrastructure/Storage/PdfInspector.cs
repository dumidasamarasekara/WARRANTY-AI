using System.IO.Compression;
using System.Text;

namespace Warranty.Infrastructure.Storage;

/// <summary>
/// Finds the PDF features an evidence file may not have (FR-009, research R11): encryption,
/// JavaScript (<c>/JavaScript</c>, <c>/JS</c>), launch actions (<c>/Launch</c>) and embedded files
/// (<c>/EmbeddedFile</c>, <c>/EmbeddedFiles</c>). A small lexer reads the file's names outside of
/// strings, comments and stream bodies — so text and image data can't cause false alarms — and
/// decodes <c>#xx</c> escapes, so <c>/J#61vaScript</c> is caught. Compressed object streams
/// (<c>/Type /ObjStm</c>), where a PDF 1.5+ file may hide its dictionaries, are inflated and read
/// the same way; one that can't be read makes the file unverifiable and so rejected.
/// </summary>
internal static class PdfInspector
{
    /// <summary>Cap on the inflated size of all object streams (an inflation-bomb guard).</summary>
    public const long MaxInflatedBytes = 64L * 1024 * 1024;

    public const string Encrypted = "the PDF is password-protected or encrypted. Please send an unprotected copy.";

    public const string JavaScript = "the PDF contains JavaScript. Please send a plain PDF or a photo of the invoice.";

    public const string LaunchAction = "the PDF contains a launch action. Please send a plain PDF or a photo of the invoice.";

    public const string EmbeddedFiles = "the PDF contains embedded files. Please send a plain PDF or a photo of the invoice.";

    public const string Unverifiable = "the PDF could not be checked. Please send a plain PDF or a photo of the invoice.";

    /// <summary>The claimant-facing reason the PDF is refused, or null when it may be stored.</summary>
    public static string? FindThreat(ReadOnlySpan<byte> pdf)
    {
        var scan = new Scan();
        Lex(pdf, scan, allowStreams: true);
        if (scan.Encrypted)
        {
            return Encrypted;
        }

        if (!scan.Unverifiable)
        {
            foreach (var stream in scan.ObjectStreams)
            {
                Lex(stream, scan, allowStreams: false);
            }
        }

        return scan.Encrypted ? Encrypted
            : scan.JavaScript ? JavaScript
            : scan.Launch ? LaunchAction
            : scan.EmbeddedFiles ? EmbeddedFiles
            : scan.Unverifiable ? Unverifiable
            : null;
    }

    private static void Lex(ReadOnlySpan<byte> pdf, Scan scan, bool allowStreams)
    {
        // Names seen since the last "obj" keyword: the dictionary in front of a "stream" keyword.
        var objectNames = new List<string>();
        var i = 0;
        while (i < pdf.Length)
        {
            var c = pdf[i];
            if (IsWhitespace(c))
            {
                i++;
            }
            else if (c == '%')
            {
                while (i < pdf.Length && pdf[i] is not ((byte)'\r' or (byte)'\n'))
                {
                    i++;
                }
            }
            else if (c == '(')
            {
                i = SkipLiteralString(pdf, i);
            }
            else if (c == '<' && i + 1 < pdf.Length && pdf[i + 1] == '<')
            {
                i += 2;
            }
            else if (c == '<')
            {
                var end = pdf[i..].IndexOf((byte)'>');
                i = end < 0 ? pdf.Length : i + end + 1;
            }
            else if (c == '/')
            {
                var start = ++i;
                while (i < pdf.Length && IsRegular(pdf[i]))
                {
                    i++;
                }

                var name = DecodeName(pdf[start..i]);
                scan.See(name);
                objectNames.Add(name);
            }
            else if (IsDelimiter(c))
            {
                i++;
            }
            else
            {
                var start = i;
                while (i < pdf.Length && IsRegular(pdf[i]))
                {
                    i++;
                }

                var token = pdf[start..i];
                if (token.SequenceEqual("obj"u8))
                {
                    objectNames.Clear();
                }
                else if (allowStreams && token.SequenceEqual("stream"u8))
                {
                    i = SkipStream(pdf, i, objectNames, scan);
                    objectNames.Clear();
                }
            }
        }
    }

    /// <summary>Skips a stream body; an object stream's body is decoded and queued for lexing.</summary>
    private static int SkipStream(ReadOnlySpan<byte> pdf, int afterKeyword, List<string> dictionaryNames, Scan scan)
    {
        var start = afterKeyword;
        if (start < pdf.Length && pdf[start] == '\r')
        {
            start++;
        }

        if (start < pdf.Length && pdf[start] == '\n')
        {
            start++;
        }

        var length = pdf[start..].IndexOf("endstream"u8);
        var end = length < 0 ? pdf.Length : start + length;
        if (dictionaryNames.Contains("ObjStm"))
        {
            QueueObjectStream(pdf[start..end], dictionaryNames, scan);
        }

        return length < 0 ? pdf.Length : end + "endstream".Length;
    }

    private static void QueueObjectStream(ReadOnlySpan<byte> body, List<string> dictionaryNames, Scan scan)
    {
        var filters = dictionaryNames.Where(n => n.EndsWith("Decode", StringComparison.Ordinal) || n is "Fl" or "AHx" or "A85" or "LZW" or "RL").ToList();
        if (filters.Count == 0)
        {
            scan.ObjectStreams.Add(body.ToArray());
            return;
        }

        if (filters is not ["FlateDecode" or "Fl"] || dictionaryNames.Contains("DecodeParms") || dictionaryNames.Contains("DP"))
        {
            scan.Unverifiable = true;
            return;
        }

        try
        {
            using var input = new MemoryStream(body.ToArray(), writable: false);
            using var zlib = new ZLibStream(input, CompressionMode.Decompress);
            using var output = new MemoryStream();
            var buffer = new byte[81_920];
            int n;
            while ((n = zlib.Read(buffer)) > 0)
            {
                scan.InflatedBytes += n;
                if (scan.InflatedBytes > MaxInflatedBytes)
                {
                    scan.Unverifiable = true;
                    return;
                }

                output.Write(buffer, 0, n);
            }

            scan.ObjectStreams.Add(output.ToArray());
        }
        catch (InvalidDataException)
        {
            scan.Unverifiable = true;
        }
    }

    /// <summary>Index after the literal string starting at <paramref name="open"/> (balanced parentheses, backslash escapes).</summary>
    private static int SkipLiteralString(ReadOnlySpan<byte> pdf, int open)
    {
        var depth = 0;
        for (var i = open; i < pdf.Length; i++)
        {
            switch (pdf[i])
            {
                case (byte)'\\':
                    i++;
                    break;
                case (byte)'(':
                    depth++;
                    break;
                case (byte)')':
                    if (--depth == 0)
                    {
                        return i + 1;
                    }

                    break;
            }
        }

        return pdf.Length;
    }

    /// <summary>A name's characters with <c>#xx</c> hex escapes decoded (PDF 1.2+).</summary>
    private static string DecodeName(ReadOnlySpan<byte> raw)
    {
        var builder = new StringBuilder(raw.Length);
        for (var i = 0; i < raw.Length; i++)
        {
            if (raw[i] == '#' && i + 2 < raw.Length && IsHex(raw[i + 1]) && IsHex(raw[i + 2]))
            {
                builder.Append((char)Convert.ToByte(Encoding.ASCII.GetString(raw.Slice(i + 1, 2)), 16));
                i += 2;
            }
            else
            {
                builder.Append((char)raw[i]);
            }
        }

        return builder.ToString();
    }

    private static bool IsHex(byte b) => char.IsAsciiHexDigit((char)b);

    private static bool IsWhitespace(byte b) => b is 0 or 9 or 10 or 12 or 13 or 32;

    private static bool IsDelimiter(byte b) => b is (byte)'(' or (byte)')' or (byte)'<' or (byte)'>' or (byte)'[' or (byte)']' or (byte)'{' or (byte)'}' or (byte)'/' or (byte)'%';

    private static bool IsRegular(byte b) => !IsWhitespace(b) && !IsDelimiter(b);

    private sealed class Scan
    {
        public bool Encrypted { get; private set; }

        public bool JavaScript { get; private set; }

        public bool Launch { get; private set; }

        public bool EmbeddedFiles { get; private set; }

        public bool Unverifiable { get; set; }

        public long InflatedBytes { get; set; }

        public List<byte[]> ObjectStreams { get; } = [];

        public void See(string name)
        {
            switch (name)
            {
                case "Encrypt":
                    Encrypted = true;
                    break;
                case "JavaScript" or "JS":
                    JavaScript = true;
                    break;
                case "Launch":
                    Launch = true;
                    break;
                case "EmbeddedFile" or "EmbeddedFiles":
                    EmbeddedFiles = true;
                    break;
            }
        }
    }
}
