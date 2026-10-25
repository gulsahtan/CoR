namespace ChainOfRepair.Core.Parsing;

// Offset-preserving lexical masking for the supported lightweight extractors.
// This is deliberately not represented as a full language grammar.
public static class SourceLexing
{
    public static string Mask(string source, bool python = false)
    {
        var result = source.ToCharArray();
        var i = 0;
        void Hide(int start, int end)
        { for (var n = start; n < end; n++) if (!char.IsWhiteSpace(result[n])) result[n] = ' '; }
        while (i < source.Length)
        {
            var start = i;
            if (python && source[i] == '#' || !python && i + 1 < source.Length && source[i] == '/' && source[i + 1] == '/')
            {
                while (i < source.Length && source[i] != '\n') i++;
                Hide(start, i); continue;
            }
            if (!python && i + 1 < source.Length && source[i] == '/' && source[i + 1] == '*')
            {
                var end = source.IndexOf("*/", i + 2, StringComparison.Ordinal);
                i = end < 0 ? source.Length : end + 2; Hide(start, i); continue;
            }
            if (source[i] is '\'' or '"')
            {
                var quote = source[i];
                var triple = python && i + 2 < source.Length && source[i + 1] == quote && source[i + 2] == quote;
                var verbatim = !python && quote == '"' && i > 0 && source[i - 1] == '@';
                i += triple ? 3 : 1;
                while (i < source.Length)
                {
                    if (!verbatim && source[i] == '\\') { i = Math.Min(source.Length, i + 2); continue; }
                    if (source[i] == quote)
                    {
                        if (triple)
                        {
                            if (i + 2 < source.Length && source[i + 1] == quote && source[i + 2] == quote) { i += 3; break; }
                        }
                        else if (verbatim && i + 1 < source.Length && source[i + 1] == quote) { i += 2; continue; }
                        else { i++; break; }
                    }
                    i++;
                }
                Hide(start, i); continue;
            }
            i++;
        }
        return new string(result);
    }

    public static int ClosingBrace(string masked, int opening)
    {
        var depth = 0;
        for (var i = opening; i < masked.Length; i++)
        {
            if (masked[i] == '{') depth++;
            if (masked[i] == '}' && --depth == 0) return i;
        }
        return masked.Length - 1;
    }
}
