using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Text;

namespace RhinoIfc.Core.Step
{
    /// <summary>The value of an omitted optional attribute is null; '*' (derived) is this.</summary>
    public sealed class StepDerived
    {
        public static readonly StepDerived Instance = new StepDerived();

        private StepDerived()
        {
        }

        public override string ToString() => "*";
    }

    /// <summary>An enumeration value such as .T., .ELEMENT. (the text without dots).</summary>
    public sealed class StepEnum
    {
        public static readonly StepEnum True = new StepEnum("T");
        public static readonly StepEnum False = new StepEnum("F");
        public static readonly StepEnum Unknown = new StepEnum("U");

        public StepEnum(string value)
        {
            Value = value;
        }

        public string Value { get; }

        public static StepEnum Of(string value)
        {
            switch (value)
            {
                case "T": return True;
                case "F": return False;
                case "U": return Unknown;
                default: return new StepEnum(value);
            }
        }

        public override string ToString() => "." + Value + ".";
    }

    /// <summary>A reference to another entity instance (#123).</summary>
    public readonly struct StepRef
    {
        public StepRef(int id)
        {
            Id = id;
        }

        public int Id { get; }

        public override string ToString() => "#" + Id.ToString(CultureInfo.InvariantCulture);
    }

    /// <summary>A typed value such as IFCLABEL('x') or IFCLENGTHMEASURE(2.5).</summary>
    public sealed class StepTyped
    {
        public StepTyped(string type, object value)
        {
            Type = type;
            Value = value;
        }

        /// <summary>Upper case: IFCLABEL.</summary>
        public string Type { get; }

        public object Value { get; }

        public override string ToString() => Type + "(" + Value + ")";
    }

    /// <summary>Parses one attribute list straight from the mapped file. Values: null ($), <see cref="StepDerived"/>,
    /// long, double, string, <see cref="StepEnum"/>, <see cref="StepRef"/>, object[] (lists), <see cref="StepTyped"/>.</summary>
    internal unsafe struct StepParser
    {
        private readonly byte* _data;
        private readonly long _length;
        private long _pos;

        public StepParser(byte* data, long offset, long length)
        {
            _data = data;
            _pos = offset;
            _length = length;
        }

        /// <summary>At the '(' of the attribute list.</summary>
        public object[] ParseArguments()
        {
            SkipSpace();
            Expect('(');
            return ParseListBody();
        }

        /// <summary>At the outer '(' of (IFCA(...)IFCB(...)): part names and all attributes in order.</summary>
        public Tuple<string[], object[]> ParseComplex()
        {
            SkipSpace();
            Expect('(');
            var names = new List<string>();
            var args = new List<object>();
            while (true)
            {
                SkipSpace();
                byte c = Peek();
                if (c == ')')
                {
                    _pos++;
                    break;
                }
                var name = ParseKeyword();
                names.Add(name);
                SkipSpace();
                Expect('(');
                args.AddRange(ParseListBody());
            }
            return Tuple.Create(names.ToArray(), args.ToArray());
        }

        private object[] ParseListBody()
        {
            SkipSpace();
            if (Peek() == ')')
            {
                _pos++;
                return Array.Empty<object>();
            }
            var items = new List<object>(8);
            while (true)
            {
                items.Add(ParseValue());
                SkipSpace();
                byte c = Next();
                if (c == ',')
                    continue;
                if (c == ')')
                    break;
                throw Error("',' oder ')' erwartet");
            }
            return items.ToArray();
        }

        private object ParseValue()
        {
            SkipSpace();
            byte c = Peek();
            switch (c)
            {
                case (byte)'$':
                    _pos++;
                    return null;
                case (byte)'*':
                    _pos++;
                    return StepDerived.Instance;
                case (byte)'#':
                    _pos++;
                    return new StepRef((int)ParseInteger());
                case (byte)'\'':
                    return ParseString();
                case (byte)'.':
                    return ParseEnum();
                case (byte)'(':
                    _pos++;
                    return ParseListBody();
                case (byte)'"':
                    return ParseBinary();
            }
            if (c == '-' || c == '+' || (c >= '0' && c <= '9'))
                return ParseNumber();
            if ((c >= 'A' && c <= 'Z') || (c >= 'a' && c <= 'z'))
            {
                var keyword = ParseKeyword();
                SkipSpace();
                Expect('(');
                var inner = ParseListBody();
                return new StepTyped(keyword, inner.Length == 1 ? inner[0] : inner);
            }
            throw Error("Wert erwartet");
        }

        private string ParseKeyword()
        {
            long start = _pos;
            while (_pos < _length)
            {
                byte c = _data[_pos];
                if ((c >= 'A' && c <= 'Z') || (c >= 'a' && c <= 'z') || (c >= '0' && c <= '9') || c == '_')
                    _pos++;
                else
                    break;
            }
            var chars = new char[_pos - start];
            for (long i = start; i < _pos; i++)
            {
                byte c = _data[i];
                chars[i - start] = (char)(c >= 'a' && c <= 'z' ? c - 32 : c);
            }
            return new string(chars);
        }

        private long ParseInteger()
        {
            long value = 0;
            bool negative = false;
            if (Peek() == '-')
            {
                negative = true;
                _pos++;
            }
            while (_pos < _length && _data[_pos] >= '0' && _data[_pos] <= '9')
                value = value * 10 + (_data[_pos++] - '0');
            return negative ? -value : value;
        }

        private object ParseNumber()
        {
            long start = _pos;
            bool isReal = false;
            if (Peek() == '-' || Peek() == '+')
                _pos++;
            while (_pos < _length)
            {
                byte c = _data[_pos];
                if (c >= '0' && c <= '9')
                    _pos++;
                else if (c == '.' || c == 'E' || c == 'e')
                {
                    isReal = true;
                    _pos++;
                    if ((c == 'E' || c == 'e') && _pos < _length && (_data[_pos] == '-' || _data[_pos] == '+'))
                        _pos++;
                }
                else
                    break;
            }
            int length = (int)(_pos - start);
            if (!isReal)
            {
                long value = 0;
                bool negative = _data[start] == '-';
                for (long i = start; i < _pos; i++)
                    if (_data[i] >= '0' && _data[i] <= '9')
                        value = value * 10 + (_data[i] - '0');
                return negative ? -value : value;
            }
            return ParseReal(_data + start, length);
        }

        /// <summary>STEP reals: 1., -0.5, 1.E-05, 3.14159E2.</summary>
        internal static double ParseReal(byte* p, int length)
        {
            // Fast path for plain decimals; anything else goes through double.Parse.
            int i = 0;
            bool negative = false;
            if (i < length && (p[i] == '-' || p[i] == '+'))
            {
                negative = p[i] == '-';
                i++;
            }
            long mantissa = 0;
            int digits = 0;
            int scale = 0;
            while (i < length && p[i] >= '0' && p[i] <= '9')
            {
                if (digits < 18)
                {
                    mantissa = mantissa * 10 + (p[i] - '0');
                    if (mantissa != 0)
                        digits++;
                }
                else
                    scale++;
                i++;
            }
            if (i < length && p[i] == '.')
            {
                i++;
                while (i < length && p[i] >= '0' && p[i] <= '9')
                {
                    if (digits < 18)
                    {
                        mantissa = mantissa * 10 + (p[i] - '0');
                        if (mantissa != 0)
                            digits++;
                        scale--;
                    }
                    i++;
                }
            }
            int exponent = 0;
            if (i < length && (p[i] == 'E' || p[i] == 'e'))
            {
                i++;
                bool expNegative = false;
                if (i < length && (p[i] == '-' || p[i] == '+'))
                {
                    expNegative = p[i] == '-';
                    i++;
                }
                while (i < length && p[i] >= '0' && p[i] <= '9')
                {
                    exponent = exponent * 10 + (p[i] - '0');
                    i++;
                    if (exponent > 10000)
                        break;
                }
                if (expNegative)
                    exponent = -exponent;
            }
            int power = scale + exponent;
            double value;
            if (digits < 16 && power >= -22 && power <= 22)
            {
                // Exact: both mantissa and 10^|power| are exactly representable doubles.
                value = power >= 0 ? mantissa * Pow10[power] : mantissa / Pow10[-power];
            }
            else
            {
                var text = new string((sbyte*)p, 0, length);
                value = double.Parse(text.EndsWith(".", StringComparison.Ordinal) ? text + "0" : text.Replace(".E", ".0E").Replace(".e", ".0e"),
                    NumberStyles.Float, CultureInfo.InvariantCulture);
                return value;
            }
            return negative ? -value : value;
        }

        private static readonly double[] Pow10 =
        {
            1e0, 1e1, 1e2, 1e3, 1e4, 1e5, 1e6, 1e7, 1e8, 1e9, 1e10, 1e11, 1e12, 1e13, 1e14, 1e15, 1e16, 1e17, 1e18, 1e19,
            1e20, 1e21, 1e22,
        };

        private StepEnum ParseEnum()
        {
            _pos++; // '.'
            long start = _pos;
            while (_pos < _length && _data[_pos] != '.')
                _pos++;
            int length = (int)(_pos - start);
            var chars = new char[length];
            for (int i = 0; i < length; i++)
            {
                byte c = _data[start + i];
                chars[i] = (char)(c >= 'a' && c <= 'z' ? c - 32 : c);
            }
            _pos++; // '.'
            return StepEnum.Of(new string(chars));
        }

        private string ParseBinary()
        {
            _pos++;
            long start = _pos;
            while (_pos < _length && _data[_pos] != '"')
                _pos++;
            var result = new string((sbyte*)(_data + start), 0, (int)(_pos - start));
            _pos++;
            return result;
        }

        private string ParseString()
        {
            _pos++; // opening quote
            var bytes = new List<byte>(32);
            while (true)
            {
                if (_pos >= _length)
                    throw Error("Zeichenkette nicht abgeschlossen");
                byte c = _data[_pos++];
                if (c == '\'')
                {
                    if (_pos < _length && _data[_pos] == '\'')
                    {
                        bytes.Add((byte)'\'');
                        _pos++;
                        continue;
                    }
                    break;
                }
                if (c == '\r' || c == '\n')
                    continue; // line breaks inside strings are not part of the value
                bytes.Add(c);
            }
            return StepString.Decode(bytes.ToArray());
        }

        private void SkipSpace()
        {
            while (_pos < _length)
            {
                byte c = _data[_pos];
                if (c == ' ' || c == '\t' || c == '\r' || c == '\n')
                    _pos++;
                else if (c == '/' && _pos + 1 < _length && _data[_pos + 1] == '*')
                {
                    _pos += 2;
                    while (_pos + 1 < _length && !(_data[_pos] == '*' && _data[_pos + 1] == '/'))
                        _pos++;
                    _pos += 2;
                }
                else
                    break;
            }
        }

        private byte Peek()
        {
            if (_pos >= _length)
                throw Error("Unerwartetes Dateiende");
            return _data[_pos];
        }

        private byte Next()
        {
            if (_pos >= _length)
                throw Error("Unerwartetes Dateiende");
            return _data[_pos++];
        }

        private void Expect(char c)
        {
            if (Next() != c)
                throw Error($"'{c}' erwartet");
        }

        private Exception Error(string message) => new InvalidDataException($"STEP-Lesefehler bei Byte {_pos}: {message}");
    }

    /// <summary>Decodes the text of a STEP string: '' was already handled; \X2\…\X0\ (UTF-16), \X4\…\X0\ (UTF-32),
    /// \X\hh (ISO 8859-1), \S\c (upper half), \\; raw non-ASCII bytes as UTF-8 when valid, else Windows-1252.</summary>
    public static class StepString
    {
        private static readonly Encoding Utf8Strict = new UTF8Encoding(false, true);

        public static string Decode(byte[] bytes)
        {
            bool hasBackslash = false, hasHigh = false;
            foreach (var b in bytes)
            {
                if (b == '\\')
                    hasBackslash = true;
                else if (b >= 0x80)
                    hasHigh = true;
            }
            string raw;
            if (!hasHigh)
                raw = Encoding.ASCII.GetString(bytes);
            else
            {
                try
                {
                    raw = Utf8Strict.GetString(bytes);
                }
                catch (DecoderFallbackException)
                {
                    raw = Latin1(bytes);
                }
            }
            return hasBackslash ? Unescape(raw) : raw;
        }

        private static string Latin1(byte[] bytes)
        {
            var chars = new char[bytes.Length];
            for (int i = 0; i < bytes.Length; i++)
                chars[i] = (char)bytes[i];
            return new string(chars);
        }

        public static string Unescape(string s)
        {
            var sb = new StringBuilder(s.Length);
            int i = 0;
            while (i < s.Length)
            {
                char c = s[i];
                if (c != '\\')
                {
                    sb.Append(c);
                    i++;
                    continue;
                }
                if (Starts(s, i, "\\\\"))
                {
                    sb.Append('\\');
                    i += 2;
                }
                else if (Starts(s, i, "\\X2\\") || Starts(s, i, "\\X4\\"))
                {
                    int width = s[i + 2] == '2' ? 4 : 8;
                    i += 4;
                    while (i + width <= s.Length && !Starts(s, i, "\\X0\\"))
                    {
                        if (!int.TryParse(s.Substring(i, width), NumberStyles.HexNumber, CultureInfo.InvariantCulture, out var code))
                            break;
                        if (width == 4)
                            sb.Append((char)code);
                        else
                            sb.Append(char.ConvertFromUtf32(code));
                        i += width;
                    }
                    if (Starts(s, i, "\\X0\\"))
                        i += 4;
                }
                else if (Starts(s, i, "\\X\\") && i + 5 <= s.Length
                    && int.TryParse(s.Substring(i + 3, 2), NumberStyles.HexNumber, CultureInfo.InvariantCulture, out var latin))
                {
                    sb.Append((char)latin);
                    i += 5;
                }
                else if (Starts(s, i, "\\S\\") && i + 3 < s.Length)
                {
                    sb.Append((char)(s[i + 3] + 128));
                    i += 4;
                }
                else if (i + 3 < s.Length && s[i + 1] == 'P' && s[i + 3] == '\\')
                {
                    i += 4; // code page switch \PA\ – ignored
                }
                else
                {
                    sb.Append(c);
                    i++;
                }
            }
            return sb.ToString();
        }

        private static bool Starts(string s, int i, string token) => string.CompareOrdinal(s, i, token, 0, token.Length) == 0;
    }
}
