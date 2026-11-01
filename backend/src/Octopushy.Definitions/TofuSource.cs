namespace Octopushy.Definitions;

/// <summary>A top-level block of an OpenTofu file, with a module's <c>source</c> when it is a plain string.</summary>
public sealed record TofuBlock(string Type, string? Source);

/// <summary>
/// Reads the top-level blocks of an OpenTofu file: comments, strings, interpolation and heredocs
/// are tokens, not text, so <c>/* x */ module "y" {</c> is a module call. It knows just enough
/// HCL for that. Anything it cannot follow makes the whole file unreadable, and callers refuse
/// an unreadable file, because what they ask decides which code runs with a password.
/// </summary>
public static class TofuSource
{
    /// <summary>The file's top-level blocks, or null when it cannot be read with certainty.</summary>
    public static IReadOnlyList<TofuBlock>? Blocks(string text)
    {
        try
        {
            return new Scanner(text).Run();
        }
        catch (FormatException)
        {
            return null;
        }
    }

    private sealed class Scanner(string text)
    {
        private int _at;

        public List<TofuBlock> Run()
        {
            var blocks = new List<TofuBlock>();
            while (true)
            {
                SkipTrivia();
                if (_at >= text.Length)
                    return blocks;
                var type = Identifier() ?? throw new FormatException();
                if (Peek() == '=')
                    throw new FormatException(); // no top-level attributes in a .tf file
                string? label = null;
                while (true)
                {
                    SkipTrivia();
                    if (Peek() == '"')
                    {
                        label = Quoted();
                        continue;
                    }

                    if (Peek() == '{')
                        break;
                    if (Identifier() is null)
                        throw new FormatException();
                }

                _ = label;
                _at++;
                blocks.Add(new TofuBlock(type, Body(type == "module")));
            }
        }

        private char Peek() => _at < text.Length ? text[_at] : '\0';

        private bool At(string s) => string.CompareOrdinal(text, _at, s, 0, s.Length) == 0;

        private void SkipTrivia()
        {
            while (_at < text.Length)
            {
                if (char.IsWhiteSpace(text[_at]))
                {
                    _at++;
                }
                else if (Peek() == '#' || At("//"))
                {
                    while (_at < text.Length && text[_at] != '\n')
                        _at++;
                }
                else if (At("/*"))
                {
                    var end = text.IndexOf("*/", _at + 2, StringComparison.Ordinal);
                    if (end < 0)
                        throw new FormatException();
                    _at = end + 2;
                }
                else
                {
                    return;
                }
            }
        }

        private string? Identifier()
        {
            SkipTrivia();
            var start = _at;
            while (_at < text.Length && (char.IsLetterOrDigit(text[_at]) || text[_at] is '_' or '-'))
                _at++;
            return _at == start ? null : text[start.._at];
        }

        /// <summary>Reads a block's body from after its brace, returning the value of a literal <c>source</c>.</summary>
        private string? Body(bool wantSource)
        {
            string? source = null;
            var depth = 1;
            while (depth > 0)
            {
                SkipTrivia();
                if (_at >= text.Length)
                    throw new FormatException();
                var c = text[_at];
                if (c is '{' or '[' or '(')
                {
                    depth++;
                    _at++;
                }
                else if (c is '}' or ']' or ')')
                {
                    depth--;
                    _at++;
                }
                else if (c == '"')
                {
                    Quoted();
                }
                else if (At("<<"))
                {
                    Heredoc();
                }
                else if (wantSource && depth == 1 && (char.IsLetter(c) || c == '_'))
                {
                    var name = Identifier();
                    SkipTrivia();
                    if (name == "source" && Peek() == '=')
                    {
                        _at++;
                        SkipTrivia();
                        if (Peek() != '"')
                            throw new FormatException();
                        var start = _at;
                        source = Quoted();
                        if (source.Contains("${", StringComparison.Ordinal) || text[start..(_at)].Contains('\\'))
                            throw new FormatException();
                    }
                }
                else
                {
                    _at++;
                }
            }

            return source;
        }

        /// <summary>Reads a string with its interpolations, which can hold strings of their own.</summary>
        private string Quoted()
        {
            var start = ++_at;
            while (true)
            {
                if (_at >= text.Length || text[_at] == '\n')
                    throw new FormatException();
                var c = text[_at];
                if (c == '\\')
                {
                    _at += 2;
                }
                else if (c == '"')
                {
                    return text[start.._at++];
                }
                else if ((c is '$' or '%') && _at + 1 < text.Length && text[_at + 1] == '{')
                {
                    _at += 2;
                    Interpolation();
                }
                else
                {
                    _at++;
                }
            }
        }

        private void Interpolation()
        {
            var depth = 1;
            while (depth > 0)
            {
                if (_at >= text.Length)
                    throw new FormatException();
                var c = text[_at];
                if (c == '"')
                {
                    Quoted();
                }
                else
                {
                    depth += c == '{' ? 1 : c == '}' ? -1 : 0;
                    _at++;
                }
            }
        }

        private void Heredoc()
        {
            _at += 2;
            if (Peek() == '-')
                _at++;
            var marker = Identifier() ?? throw new FormatException();
            var line = text.IndexOf('\n', _at);
            while (line >= 0)
            {
                var next = text.IndexOf('\n', line + 1);
                var content = (next < 0 ? text[(line + 1)..] : text[(line + 1)..next]).Trim();
                if (content == marker)
                {
                    _at = next < 0 ? text.Length : next;
                    return;
                }

                line = next;
            }

            throw new FormatException();
        }
    }
}
