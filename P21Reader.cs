using System;
using System.Collections.Generic;
using System.Globalization;
using System.Text;

namespace TeklaToStep;

public abstract class P21Value
{
    public virtual double? AsNumber() => null;
    public virtual int? AsRef() => null;
    public virtual string? AsString() => null;
    public virtual string? AsEnum() => null;
    public virtual bool? AsBool() => null;
    public virtual IReadOnlyList<P21Value>? AsList() => null;
    public virtual bool IsNull => false;
}

public sealed class P21Null : P21Value
{
    public static readonly P21Null Instance = new P21Null();
    public override bool IsNull => true;
}

public sealed class P21Derived : P21Value
{
    public static readonly P21Derived Instance = new P21Derived();
    public override bool IsNull => true;
}

public sealed class P21Ref : P21Value
{
    public readonly int Id;
    public P21Ref(int id) { Id = id; }
    public override int? AsRef() => Id;
}

public sealed class P21Number : P21Value
{
    public readonly double Value;
    public P21Number(double value) { Value = value; }
    public override double? AsNumber() => Value;
}

public sealed class P21String : P21Value
{
    public readonly string Value;
    public P21String(string value) { Value = value ?? string.Empty; }
    public override string? AsString() => Value;
}

public sealed class P21Enum : P21Value
{
    public readonly string Name;
    public P21Enum(string name) { Name = name ?? string.Empty; }
    public override string? AsEnum() => Name;
    public override bool? AsBool()
    {
        if (Name == "T") return true;
        if (Name == "F") return false;
        return null;
    }
}

public sealed class P21List : P21Value
{
    public readonly List<P21Value> Items;
    public P21List(List<P21Value> items) { Items = items ?? new List<P21Value>(); }
    public override IReadOnlyList<P21Value>? AsList() => Items;
}

public sealed class P21Typed : P21Value
{
    public readonly string Type;
    public readonly P21Value Inner;
    public P21Typed(string type, P21Value inner)
    {
        Type = type ?? string.Empty;
        Inner = inner ?? P21Null.Instance;
    }
    public override double? AsNumber() => Inner.AsNumber();
    public override string? AsString() => Inner.AsString();
    public override bool? AsBool() => Inner.AsBool();
    public override IReadOnlyList<P21Value>? AsList() => Inner.AsList();
    public override int? AsRef() => Inner.AsRef();
}

public sealed class P21Entity
{
    public int Id;
    public string Type = string.Empty;
    public List<P21Value> Args = new List<P21Value>();
}

/// <summary>ISO 10303-21 clear-text reader. Used for Tekla IFC and for checking our own STEP.</summary>
public sealed class P21File
{
    public readonly Dictionary<int, P21Entity> Entities = new Dictionary<int, P21Entity>();

    public P21Entity? Get(int id)
    {
        P21Entity e;
        return Entities.TryGetValue(id, out e) ? e : null;
    }

    public P21Entity? Resolve(P21Value? value)
    {
        if (value == null) return null;
        if (value is P21Typed typed) return Resolve(typed.Inner);
        int? id = value.AsRef();
        if (id == null) return null;
        return Get(id.Value);
    }

    public static P21File Parse(string text)
    {
        if (text == null) throw new ArgumentNullException(nameof(text));
        var file = new P21File();
        int n = text.Length;
        int i = 0;
        bool inData = false;
        while (i < n)
        {
            SkipWsAndComments(text, ref i);
            if (i >= n) break;
            if (!inData)
            {
                if (MatchWord(text, i, "DATA"))
                {
                    i += 4;
                    SkipWsAndComments(text, ref i);
                    if (i < n && text[i] == ';') i++;
                    inData = true;
                    continue;
                }
                i++;
                continue;
            }
            if (MatchWord(text, i, "ENDSEC"))
            {
                inData = false;
                i += 6;
                continue;
            }
            if (text[i] != '#')
            {
                i++;
                continue;
            }
            i++;
            int id = ReadInt(text, ref i);
            SkipWsAndComments(text, ref i);
            if (i >= n || text[i] != '=') continue;
            i++;
            SkipWsAndComments(text, ref i);
            var entity = new P21Entity { Id = id };
            if (i < n && text[i] == '(')
            {
                i++;
                string bestType = string.Empty;
                List<P21Value> bestArgs = new List<P21Value>();
                while (i < n && text[i] != ')')
                {
                    SkipWsAndComments(text, ref i);
                    if (i < n && text[i] == ')') break;
                    string type = ReadTypeName(text, ref i);
                    SkipWsAndComments(text, ref i);
                    var args = new List<P21Value>();
                    if (i < n && text[i] == '(')
                        args = ReadParen(text, ref i);
                    if (type.Length > 0)
                    {
                        bestType = type;
                        bestArgs = args;
                    }
                    SkipWsAndComments(text, ref i);
                }
                if (i < n && text[i] == ')') i++;
                entity.Type = bestType;
                entity.Args = bestArgs;
            }
            else
            {
                entity.Type = ReadTypeName(text, ref i);
                SkipWsAndComments(text, ref i);
                if (i < n && text[i] == '(')
                    entity.Args = ReadParen(text, ref i);
            }
            SkipWsAndComments(text, ref i);
            if (i < n && text[i] == ';') i++;
            if (entity.Type.Length > 0)
                file.Entities[id] = entity;
        }
        return file;
    }

    static List<P21Value> ReadParen(string t, ref int i)
    {
        var items = new List<P21Value>();
        i++;
        while (i < t.Length)
        {
            SkipWsAndComments(t, ref i);
            if (i >= t.Length) break;
            if (t[i] == ')')
            {
                i++;
                break;
            }
            if (t[i] == ',')
            {
                i++;
                continue;
            }
            items.Add(ReadValue(t, ref i));
            SkipWsAndComments(t, ref i);
            if (i < t.Length && t[i] == ',') i++;
        }
        return items;
    }

    static P21Value ReadValue(string t, ref int i)
    {
        SkipWsAndComments(t, ref i);
        if (i >= t.Length) return P21Null.Instance;
        char c = t[i];
        if (c == '$')
        {
            i++;
            return P21Null.Instance;
        }
        if (c == '*')
        {
            i++;
            return P21Derived.Instance;
        }
        if (c == '#')
        {
            i++;
            return new P21Ref(ReadInt(t, ref i));
        }
        if (c == '(') return new P21List(ReadParen(t, ref i));
        if (c == '\'') return new P21String(ReadString(t, ref i));
        if (c == '.') return ReadEnumOrNumber(t, ref i);
        if (c == '+' || c == '-' || char.IsDigit(c)) return new P21Number(ReadNumber(t, ref i));
        if (char.IsLetter(c))
        {
            string type = ReadTypeName(t, ref i);
            SkipWsAndComments(t, ref i);
            if (i < t.Length && t[i] == '(')
            {
                var inner = ReadParen(t, ref i);
                P21Value payload = inner.Count == 1 ? inner[0] : new P21List(inner);
                return new P21Typed(type, payload);
            }
            return new P21String(type);
        }
        i++;
        return P21Null.Instance;
    }

    static P21Value ReadEnumOrNumber(string t, ref int i)
    {
        if (i + 1 < t.Length && char.IsDigit(t[i + 1]))
            return new P21Number(ReadNumber(t, ref i));
        i++;
        int start = i;
        while (i < t.Length && t[i] != '.') i++;
        string name = t.Substring(start, i - start).ToUpperInvariant();
        if (i < t.Length && t[i] == '.') i++;
        return new P21Enum(name);
    }

    static string ReadString(string t, ref int i)
    {
        i++;
        var sb = new StringBuilder();
        while (i < t.Length)
        {
            char c = t[i++];
            if (c == '\'')
            {
                if (i < t.Length && t[i] == '\'')
                {
                    sb.Append('\'');
                    i++;
                    continue;
                }
                break;
            }
            if (c == '\\')
            {
                sb.Append(ReadEscape(t, ref i));
                continue;
            }
            sb.Append(c);
        }
        return sb.ToString();
    }

    static string ReadEscape(string t, ref int i)
    {
        if (i >= t.Length) return "\\";
        if (MatchWord(t, i, "X2\\") || (i + 2 < t.Length && t[i] == 'X' && t[i + 1] == '2' && t[i + 2] == '\\'))
        {
            i += 3;
            var sb = new StringBuilder();
            while (i + 4 <= t.Length && t[i] != '\\')
            {
                int code;
                if (!int.TryParse(t.Substring(i, 4), NumberStyles.HexNumber, CultureInfo.InvariantCulture, out code))
                    break;
                sb.Append((char)code);
                i += 4;
            }
            if (i + 4 <= t.Length && t[i] == '\\' && t[i + 1] == 'X' && t[i + 2] == '0' && t[i + 3] == '\\')
                i += 4;
            else if (i < t.Length && t[i] == '\\')
                i++;
            return sb.ToString();
        }
        if (i + 2 < t.Length && t[i] == 'X' && t[i + 1] == '\\')
        {
            i += 2;
            if (i + 2 <= t.Length)
            {
                int code;
                if (int.TryParse(t.Substring(i, 2), NumberStyles.HexNumber, CultureInfo.InvariantCulture, out code))
                {
                    i += 2;
                    return ((char)code).ToString();
                }
            }
        }
        if (i + 1 < t.Length && t[i] == 'S' && t[i + 1] == '\\')
        {
            i += 2;
            if (i < t.Length) return ((char)(t[i++] + 128)).ToString();
        }
        if (t[i] == '\\')
        {
            i++;
            return string.Empty;
        }
        return "\\";
    }

    static double ReadNumber(string t, ref int i)
    {
        int start = i;
        if (i < t.Length && (t[i] == '+' || t[i] == '-')) i++;
        while (i < t.Length && char.IsDigit(t[i])) i++;
        if (i < t.Length && t[i] == '.')
        {
            i++;
            while (i < t.Length && char.IsDigit(t[i])) i++;
        }
        if (i < t.Length && (t[i] == 'E' || t[i] == 'e'))
        {
            i++;
            if (i < t.Length && (t[i] == '+' || t[i] == '-')) i++;
            while (i < t.Length && char.IsDigit(t[i])) i++;
        }
        double value;
        if (!double.TryParse(t.Substring(start, i - start), NumberStyles.Float, CultureInfo.InvariantCulture, out value))
            return 0;
        return value;
    }

    static int ReadInt(string t, ref int i)
    {
        int start = i;
        while (i < t.Length && char.IsDigit(t[i])) i++;
        int value;
        if (!int.TryParse(t.Substring(start, i - start), NumberStyles.Integer, CultureInfo.InvariantCulture, out value))
            return 0;
        return value;
    }

    static string ReadTypeName(string t, ref int i)
    {
        int start = i;
        while (i < t.Length && (char.IsLetterOrDigit(t[i]) || t[i] == '_')) i++;
        return t.Substring(start, i - start).ToUpperInvariant();
    }

    static void SkipWsAndComments(string t, ref int i)
    {
        while (i < t.Length)
        {
            char c = t[i];
            if (c == ' ' || c == '\t' || c == '\r' || c == '\n')
            {
                i++;
                continue;
            }
            if (c == '/' && i + 1 < t.Length && t[i + 1] == '*')
            {
                i += 2;
                while (i + 1 < t.Length && !(t[i] == '*' && t[i + 1] == '/')) i++;
                if (i + 1 < t.Length) i += 2;
                continue;
            }
            break;
        }
    }

    static bool MatchWord(string t, int i, string word)
    {
        if (i + word.Length > t.Length) return false;
        for (int k = 0; k < word.Length; k++)
        {
            char c = t[i + k];
            if (c >= 'a' && c <= 'z') c = (char)(c - 32);
            if (c != word[k]) return false;
        }
        return true;
    }
}
