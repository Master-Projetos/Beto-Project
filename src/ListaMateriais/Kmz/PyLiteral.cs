using System.Globalization;
using System.Text;

namespace ListaMateriais.Kmz;

/// <summary>A dict literal: its entries in the order they were written (keys may repeat or be tuples).</summary>
internal sealed record PyDict(IReadOnlyList<KeyValuePair<object?, object?>> Entries);

/// <summary>
/// Parses a Python (or JSON) literal: dicts, lists, tuples, strings, numbers, True/False/None.
/// Values come back as <see cref="PyDict"/>, <c>List&lt;object?&gt;</c>, string, double, bool or null.
/// Nothing is evaluated: names, calls and operators are rejected.
/// </summary>
internal sealed class PyLiteral
{
    private const int MaxDepth = 64;

    private readonly string _text;
    private int _at;
    private int _depth;

    private PyLiteral(string text) => _text = text;

    /// <exception cref="FormatException">The text is not a literal; the message says where and why, in Portuguese.</exception>
    public static object? Parse(string text)
    {
        var parser = new PyLiteral(text);
        parser.SkipSpace();
        parser.SkipAssignment();
        var value = parser.Value();
        parser.SkipSpace();
        if (parser._at < text.Length)
            throw parser.Error($"texto sobrando depois do fim: \"{parser.Peek(12)}\"");
        return value;
    }

    /// <summary>"pontos = {...}" is accepted: people paste the whole line of code.</summary>
    private void SkipAssignment()
    {
        var start = _at;
        while (_at < _text.Length && (char.IsLetterOrDigit(_text[_at]) || _text[_at] == '_'))
            _at++;
        var named = _at > start;
        SkipSpace();
        if (named && _at < _text.Length && _text[_at] == '=' && !(_at + 1 < _text.Length && _text[_at + 1] == '='))
        {
            _at++;
            SkipSpace();
            return;
        }
        _at = start;
    }

    private object? Value()
    {
        SkipSpace();
        if (_at >= _text.Length)
            throw Error("o texto terminou antes da hora");
        var c = _text[_at];
        return c switch
        {
            '{' => Dict(),
            '[' => Sequence(']'),
            '(' => Sequence(')'),
            '"' or '\'' => String(),
            _ when c is '-' or '+' or '.' || char.IsAsciiDigit(c) => Number(),
            _ when char.IsLetter(c) => Word(),
            _ => throw Error($"caractere inesperado '{c}'"),
        };
    }

    private PyDict Dict()
    {
        Enter();
        _at++;
        var entries = new List<KeyValuePair<object?, object?>>();
        while (true)
        {
            SkipSpace();
            if (Take('}'))
                break;
            var key = Value();
            SkipSpace();
            if (!Take(':'))
                throw Error("faltou ':' depois da chave");
            entries.Add(new(key, Value()));
            SkipSpace();
            if (Take(','))
                continue;
            if (Take('}'))
                break;
            throw Error("faltou ',' ou '}'");
        }
        _depth--;
        return new PyDict(entries);
    }

    private List<object?> Sequence(char close)
    {
        Enter();
        _at++;
        var items = new List<object?>();
        while (true)
        {
            SkipSpace();
            if (Take(close))
                break;
            items.Add(Value());
            SkipSpace();
            if (Take(','))
                continue;
            if (Take(close))
                break;
            throw Error($"faltou ',' ou '{close}'");
        }
        _depth--;
        return items;
    }

    private string String()
    {
        var quote = _text[_at++];
        var result = new StringBuilder();
        while (_at < _text.Length)
        {
            var c = _text[_at++];
            if (c == quote)
                return result.ToString();
            if (c != '\\' || _at >= _text.Length)
            {
                result.Append(c);
                continue;
            }
            var escaped = _text[_at++];
            switch (escaped)
            {
                case 'n': result.Append('\n'); break;
                case 't': result.Append('\t'); break;
                case 'r': result.Append('\r'); break;
                case 'u' when _at + 4 <= _text.Length
                              && int.TryParse(_text.AsSpan(_at, 4), NumberStyles.HexNumber, CultureInfo.InvariantCulture, out var code):
                    result.Append((char)code);
                    _at += 4;
                    break;
                default: result.Append(escaped); break;
            }
        }
        throw Error("texto entre aspas sem fechar");
    }

    private double Number()
    {
        var start = _at;
        while (_at < _text.Length && (char.IsAsciiLetterOrDigit(_text[_at]) || _text[_at] is '-' or '+' or '.' or '_'))
            _at++;
        var token = _text[start.._at].Replace("_", string.Empty);
        if (double.TryParse(token, NumberStyles.Float, CultureInfo.InvariantCulture, out var number) && double.IsFinite(number))
            return number;
        _at = start;
        throw Error($"número inválido \"{token}\"");
    }

    private object? Word()
    {
        var start = _at;
        while (_at < _text.Length && (char.IsLetterOrDigit(_text[_at]) || _text[_at] == '_'))
            _at++;
        var word = _text[start.._at];
        switch (word)
        {
            case "None" or "null": return null;
            case "True" or "true": return true;
            case "False" or "false": return false;
        }
        // r"..." / u'...' / b"..." prefixes are read as plain strings.
        if (word.Length <= 2 && _at < _text.Length && _text[_at] is '"' or '\'')
            return String();
        _at = start;
        throw Error($"\"{word}\" não é um valor (textos precisam de aspas)");
    }

    private void Enter()
    {
        if (++_depth > MaxDepth)
            throw Error("estrutura aninhada demais");
    }

    private bool Take(char c)
    {
        if (_at >= _text.Length || _text[_at] != c)
            return false;
        _at++;
        return true;
    }

    private void SkipSpace()
    {
        while (_at < _text.Length)
        {
            if (char.IsWhiteSpace(_text[_at]))
                _at++;
            else if (_text[_at] == '#')
                while (_at < _text.Length && _text[_at] != '\n')
                    _at++;
            else
                break;
        }
    }

    private string Peek(int length) => _text.Substring(_at, Math.Min(length, _text.Length - _at)).Trim();

    private FormatException Error(string what)
    {
        var line = 1 + _text.AsSpan(0, Math.Min(_at, _text.Length)).Count('\n');
        return new FormatException($"Linha {line}: {what}.");
    }
}
