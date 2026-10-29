using System.Text;

namespace Il2cppConsoleMod.Inspector;

/// <summary>
/// Rewrites <c>$n</c> (an inspector handle) and <c>$_</c> (last console result) in console
/// code into registry lookups cast to the object's runtime type, so its members can be used directly.
///
/// Strings, chars and comments are skipped. Holes inside interpolated strings are not
/// rewritten; assign the handle to a variable first.
/// </summary>
public static class HandleRewriter
{
    private const string Registry = "global::Il2cppConsoleMod.Inspector.ObjectRegistry";

    public readonly record struct Shift(int Position, int Delta);

    public static string Rewrite(string code) => Rewrite(code, out _);

    /// <param name="shifts">Original position and length change of every replacement, for mapping cursor positions.</param>
    public static string Rewrite(string code, out List<Shift> shifts)
    {
        shifts = new List<Shift>();
        if (code.IndexOf('$') < 0)
            return code;

        var sb = new StringBuilder(code.Length + 64);
        var i = 0;
        while (i < code.Length)
        {
            var c = code[i];
            var next = i + 1 < code.Length ? code[i + 1] : '\0';

            if (c == '/' && next == '/')
            {
                i = Copy(code, sb, i, SkipLine(code, i));
                continue;
            }

            if (c == '/' && next == '*')
            {
                var end = code.IndexOf("*/", i + 2, StringComparison.Ordinal);
                i = Copy(code, sb, i, end < 0 ? code.Length : end + 2);
                continue;
            }

            if (c == '\'')
            {
                i = Copy(code, sb, i, SkipQuoted(code, i, '\''));
                continue;
            }

            if (c == '"' || ((c == '@' || c == '$') && IsStringPrefix(code, i)))
            {
                i = Copy(code, sb, i, SkipString(code, i));
                continue;
            }

            if (c == '$' && (i == 0 || !IsIdentifierChar(code[i - 1])))
            {
                var j = i + 1;
                while (j < code.Length && char.IsDigit(code[j]))
                    j++;

                string? replacement = null;
                if (j > i + 1 && (j >= code.Length || !IsIdentifierChar(code[j])))
                    replacement = HandleExpression(int.Parse(code.AsSpan(i + 1, j - i - 1)));
                else if (next == '_' && (i + 2 >= code.Length || !IsIdentifierChar(code[i + 2])))
                {
                    j = i + 2;
                    replacement = LastExpression();
                }

                if (replacement != null)
                {
                    shifts.Add(new Shift(i, replacement.Length - (j - i)));
                    sb.Append(replacement);
                    i = j;
                    continue;
                }
            }

            sb.Append(c);
            i++;
        }

        return sb.ToString();
    }

    /// <summary>Maps a position in the original code to the rewritten code.</summary>
    public static int MapPosition(int position, List<Shift> shifts)
    {
        var mapped = position;
        foreach (var shift in shifts)
        {
            if (shift.Position < position)
                mapped += shift.Delta;
        }

        return mapped;
    }

    private static string HandleExpression(int id)
    {
        var access = $"{Registry}.Get({id})";
        if (!ObjectRegistry.TryGet(id, out var value))
            return access;
        return Typed(value, access);
    }

    private static string LastExpression()
    {
        const string access = Registry + ".Last";
        var value = ObjectRegistry.Last;
        return value == null ? access : Typed(value, access);
    }

    private static string Typed(object value, string access)
    {
        // A type handle stands for the type itself, so `$7.Instance` reads a static member.
        if (value is StaticTypeRef typeRef && TypeNames.NameableType(typeRef.Type) == typeRef.Type)
            return TypeNames.CSharp(typeRef.Type);

        var nameable = TypeNames.NameableType(value.GetType());
        return nameable == null ? access : $"(({TypeNames.CSharp(nameable)}){access})";
    }

    private static bool IsIdentifierChar(char c) => char.IsLetterOrDigit(c) || c == '_';

    private static bool IsStringPrefix(string code, int i)
    {
        // $"  @"  $@"  @$"  $$"""
        var j = i;
        while (j < code.Length && (code[j] == '$' || code[j] == '@'))
            j++;
        return j < code.Length && code[j] == '"' && j > i;
    }

    private static int SkipLine(string code, int i)
    {
        var end = code.IndexOf('\n', i);
        return end < 0 ? code.Length : end;
    }

    private static int SkipQuoted(string code, int i, char quote)
    {
        var j = i + 1;
        while (j < code.Length && code[j] != quote && code[j] != '\n')
            j += code[j] == '\\' ? 2 : 1;
        return Math.Min(code.Length, j + 1);
    }

    private static int SkipString(string code, int i)
    {
        var j = i;
        var verbatim = false;
        var interpolated = false;
        while (code[j] != '"')
        {
            verbatim |= code[j] == '@';
            interpolated |= code[j] == '$';
            j++;
        }

        // Raw string literal: """ ... """
        if (code.AsSpan(j).StartsWith("\"\"\""))
        {
            var quotes = 0;
            while (j < code.Length && code[j] == '"')
            {
                quotes++;
                j++;
            }

            var fence = new string('"', quotes);
            var end = code.IndexOf(fence, j, StringComparison.Ordinal);
            return end < 0 ? code.Length : end + quotes;
        }

        j++;
        var depth = 0;
        while (j < code.Length)
        {
            var c = code[j];
            if (interpolated && c == '{')
            {
                if (j + 1 < code.Length && code[j + 1] == '{' && depth == 0)
                    j += 2;
                else
                {
                    depth++;
                    j++;
                }

                continue;
            }

            if (interpolated && c == '}' && depth > 0)
            {
                depth--;
                j++;
                continue;
            }

            if (depth == 0)
            {
                if (verbatim && c == '"')
                {
                    if (j + 1 < code.Length && code[j + 1] == '"')
                    {
                        j += 2;
                        continue;
                    }

                    return j + 1;
                }

                if (!verbatim && c == '\\')
                {
                    j += 2;
                    continue;
                }

                if (c == '"')
                    return j + 1;
                if (!verbatim && c == '\n')
                    return j;
            }

            j++;
        }

        return code.Length;
    }

    private static int Copy(string code, StringBuilder sb, int from, int to)
    {
        sb.Append(code, from, to - from);
        return to;
    }
}
