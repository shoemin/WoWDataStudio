using System.Globalization;
using System.Text;

namespace WoWData.Core.Maps;

/// <summary>A Python tuple for <see cref="Py.Repr"/>: printed as "(a, b)".</summary>
public sealed class PyTuple
{
    public object?[] Items { get; }
    public PyTuple(params object?[] items) => Items = items;
}

/// <summary>An ordered Python dict for <see cref="Py.Repr"/>: printed as "{'k': v, ...}".</summary>
public sealed class PyDict
{
    public List<KeyValuePair<string, object?>> Items { get; } = new();
    public PyDict Add(string key, object? value)
    {
        Items.Add(new(key, value));
        return this;
    }
}

/// <summary>
/// The validator's and the diff's messages are written exactly like the Python reference (wowdata/maps), so the
/// parity tests can compare them word for word. This formats values the way Python's str() / repr() and the %-format
/// codes do: lists "['a', 'b']", tuples "(1, 2)", floats with Python's shortest repr ("1.0", "1e-05"), None, True.
/// </summary>
public static class Py
{
    private static readonly CultureInfo Inv = CultureInfo.InvariantCulture;

    public static string Repr(object? v) => v switch
    {
        null => "None",
        string s => StrRepr(s),
        bool b => b ? "True" : "False",
        float f => FloatRepr(f),
        double d => FloatRepr(d),
        PyTuple t => t.Items.Length == 1 ? "(" + Repr(t.Items[0]) + ",)" : "(" + string.Join(", ", t.Items.Select(Repr)) + ")",
        PyDict dict => "{" + string.Join(", ", dict.Items.Select(kv => StrRepr(kv.Key) + ": " + Repr(kv.Value))) + "}",
        System.Collections.IEnumerable e => "[" + string.Join(", ", e.Cast<object?>().Select(Repr)) + "]",
        IFormattable f => f.ToString(null, Inv),
        _ => v.ToString() ?? "",
    };

    /// <summary>Python's %s: str() (strings as they are, everything else as repr).</summary>
    public static string Str(object? v) => v is string s ? s : Repr(v);

    /// <summary>repr() of a str: single quotes unless the text has a ' and no ", backslash escapes.</summary>
    public static string StrRepr(string s)
    {
        char q = s.Contains('\'') && !s.Contains('"') ? '"' : '\'';
        var sb = new StringBuilder(s.Length + 2);
        sb.Append(q);
        foreach (char c in s)
        {
            if (c == q || c == '\\') sb.Append('\\').Append(c);
            else if (c == '\n') sb.Append("\\n");
            else if (c == '\r') sb.Append("\\r");
            else if (c == '\t') sb.Append("\\t");
            else if (c < 0x20 || c == 0x7F || (c >= 0x80 && c < 0xA0)) sb.Append("\\x").Append(((int)c).ToString("x2"));
            else if (c == 0xAD) sb.Append("\\xad");
            else sb.Append(c);
        }
        sb.Append(q);
        return sb.ToString();
    }

    /// <summary>repr() of a float (a float32 read from a file is a double in Python).</summary>
    public static string FloatRepr(double d)
    {
        if (double.IsNaN(d)) return "nan";
        if (double.IsPositiveInfinity(d)) return "inf";
        if (double.IsNegativeInfinity(d)) return "-inf";
        if (d == 0) return BitConverter.DoubleToInt64Bits(d) < 0 ? "-0.0" : "0.0";
        // shortest round-trip digits, then Python's layout: fixed for 1e-4 <= |x| < 1e16, else scientific
        var shortest = d.ToString("R", Inv);
        string digits;
        int exp;
        bool neg = d < 0;
        {
            var s = shortest.TrimStart('-');
            int ePos = s.IndexOfAny(new[] { 'E', 'e' });
            int e10 = 0;
            if (ePos >= 0)
            {
                e10 = int.Parse(s[(ePos + 1)..], Inv);
                s = s[..ePos];
            }
            int dot = s.IndexOf('.');
            string intPart = dot < 0 ? s : s[..dot], frac = dot < 0 ? "" : s[(dot + 1)..];
            string all = (intPart + frac).TrimStart('0');
            int lead = (intPart + frac).Length - (intPart + frac).TrimStart('0').Length;
            // decimal exponent of the first significant digit
            exp = intPart.Length - 1 - lead + e10;
            digits = all.TrimEnd('0');
            if (digits.Length == 0) digits = "0";
        }
        string body;
        if (exp >= -4 && exp < 16)
        {
            if (exp >= 0)
            {
                string ip = digits.Length > exp + 1 ? digits[..(exp + 1)] : digits.PadRight(exp + 1, '0');
                string fp = digits.Length > exp + 1 ? digits[(exp + 1)..] : "0";
                body = ip + "." + fp;
            }
            else body = "0." + new string('0', -exp - 1) + digits;
        }
        else body = Sci(digits, exp);
        return (neg ? "-" : "") + body;
    }

    private static string Sci(string digits, int exp)
    {
        string m = digits.Length == 1 ? digits : digits[0] + "." + digits[1..];
        return m + "e" + (exp < 0 ? "-" : "+") + Math.Abs(exp).ToString("00", Inv);
    }

    /// <summary>'%.4f' with the trailing zeros and dot stripped (diff.py's _f for floats).</summary>
    public static string F4(double v) => v.ToString("F4", Inv).TrimEnd('0').TrimEnd('.');

    /// <summary>Python's '%x'.</summary>
    public static string Hex(long v) => v < 0 ? "-" + (-v).ToString("x", Inv) : v.ToString("x", Inv);

    public static string Fixed(double v, int decimals) => v.ToString("F" + decimals, Inv);
}
