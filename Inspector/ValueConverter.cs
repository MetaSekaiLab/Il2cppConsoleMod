using System.Globalization;
using System.Reflection;
using System.Runtime.CompilerServices;
using System.Text.Json;
using Il2CppInterop.Runtime;
using Il2CppInterop.Runtime.InteropTypes;

namespace Il2cppConsoleMod.Inspector;

/// <summary>
/// Turns UI input (JSON strings, numbers, <c>{"h": 12}</c> handle refs or <c>$12</c>) into
/// values of a target type, and decides which inline editor a type gets.
/// </summary>
public static class ValueConverter
{
    private const int MaxEnumOptions = 400;

    public static string? EditorFor(Type type, out string[]? options)
    {
        options = null;
        type = Nullable.GetUnderlyingType(type) ?? type;

        if (type == typeof(bool))
            return "bool";
        if (type == typeof(string))
            return "str";
        if (type == typeof(char))
            return "char";
        if (type.IsEnum)
        {
            if (type.IsDefined(typeof(FlagsAttribute), false))
                return "flags";
            var names = Enum.GetNames(type);
            if (names.Length > MaxEnumOptions)
                return "flags";
            options = names;
            return "enum";
        }

        if (IsNumeric(type))
            return "num";
        if (IsColor(type))
            return "color";
        if (VectorFields(type) is { } fields)
        {
            options = fields.Select(f => f.Name).ToArray();
            return "vec";
        }

        return null;
    }

    public static object? Convert(JsonElement element, Type target)
    {
        if (target.IsByRef)
            target = target.GetElementType()!;

        switch (element.ValueKind)
        {
            case JsonValueKind.Null:
            case JsonValueKind.Undefined:
                return target.IsValueType && Nullable.GetUnderlyingType(target) == null ? Activator.CreateInstance(target) : null;
            case JsonValueKind.Object when element.TryGetProperty("h", out var handle):
                return FromHandle(handle.GetInt32(), target);
            case JsonValueKind.True:
            case JsonValueKind.False:
                return Parse(element.GetBoolean() ? "true" : "false", target);
            case JsonValueKind.Number:
                return Parse(element.GetRawText(), target);
            case JsonValueKind.String:
                return Parse(element.GetString() ?? "", target);
            default:
                throw new FormatException("unsupported value " + element.GetRawText());
        }
    }

    public static object? Parse(string text, Type target)
    {
        var trimmed = text.Trim();
        if (trimmed.Length > 1 && trimmed[0] == '$' && int.TryParse(trimmed.AsSpan(1), out var id))
            return FromHandle(id, target);

        var underlying = Nullable.GetUnderlyingType(target);
        if (underlying != null)
        {
            if (trimmed.Length == 0 || trimmed == "null")
                return null;
            target = underlying;
        }

        if (target == typeof(string))
            return text;
        if (trimmed == "null" && !target.IsValueType)
            return null;
        if (target == typeof(bool))
            return trimmed switch { "1" => true, "0" => false, _ => bool.Parse(trimmed) };
        if (target == typeof(char))
            return text.Length == 1 ? text[0] : throw new FormatException("expected a single character");
        if (target.IsEnum)
            return Enum.Parse(target, trimmed, ignoreCase: true);
        if (IsNumeric(target))
            return ParseNumber(trimmed, target);
        if (target == typeof(Type))
            return TypeCatalog.Resolve(trimmed) ?? TypeCatalog.Search(trimmed, 1).FirstOrDefault()?.Type
                ?? throw new TypeLoadException("type not found: " + trimmed);
        if (target.FullName == "Il2CppSystem.Type")
            return Il2CppTypeOf(Parse(trimmed, typeof(Type)) as Type ?? throw new TypeLoadException(trimmed));
        if (IsColor(target))
            return ParseColor(trimmed, target);
        if (VectorFields(target) is { } fields)
            return ParseVector(trimmed, target, fields);

        throw new FormatException($"cannot turn '{ValueDescriber.Truncate(text, 60)}' into {TypeNames.Display(target)}; pass a handle such as $12");
    }

    private static object? FromHandle(int id, Type target)
    {
        var value = ObjectRegistry.Get(id);
        if (target.IsInstanceOfType(value))
            return value;

        // Same native object, different wrapper type.
        if (value is Il2CppObjectBase native && typeof(Il2CppObjectBase).IsAssignableFrom(target))
            return Il2CppSupport.Wrap(target, native.Pointer);

        if (value is StaticTypeRef typeRef)
            return target.FullName == "Il2CppSystem.Type" ? Il2CppTypeOf(typeRef.Type) : typeRef.Type;

        throw new InvalidCastException($"${id} is {TypeNames.Display(value.GetType())}, expected {TypeNames.Display(target)}");
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    private static object Il2CppTypeOf(Type type) => Il2CppType.From(type);

    /// <summary>Color / Color32 shaped structs: public r, g, b, a fields.</summary>
    public static bool IsColor(Type type)
        => type.IsValueType && type.Name is "Color" or "Color32"
           && new[] { "r", "g", "b", "a" }.All(n => type.GetField(n, BindingFlags.Public | BindingFlags.Instance) != null);

    public static bool IsColor32(Type type) => IsColor(type) && type.GetField("r")!.FieldType == typeof(byte);

    public static bool IsNumeric(Type type)
        => type == typeof(int) || type == typeof(float) || type == typeof(double) || type == typeof(long)
           || type == typeof(short) || type == typeof(byte) || type == typeof(uint) || type == typeof(ulong)
           || type == typeof(ushort) || type == typeof(sbyte) || type == typeof(decimal);

    private static object ParseNumber(string text, Type target)
    {
        if (!IsInteger(target))
            return System.Convert.ChangeType(double.Parse(text, NumberStyles.Float, CultureInfo.InvariantCulture), target, CultureInfo.InvariantCulture);

        if (text.StartsWith("0x", StringComparison.OrdinalIgnoreCase))
            return System.Convert.ChangeType(ulong.Parse(text.AsSpan(2), NumberStyles.HexNumber, CultureInfo.InvariantCulture), target, CultureInfo.InvariantCulture);

        var number = decimal.Parse(text, NumberStyles.Float, CultureInfo.InvariantCulture);
        if (number != decimal.Truncate(number))
            throw new FormatException($"{TypeNames.Display(target)} needs a whole number");
        return System.Convert.ChangeType(number, target, CultureInfo.InvariantCulture);
    }

    private static bool IsInteger(Type type)
        => type != typeof(float) && type != typeof(double) && type != typeof(decimal);

    /// <summary>Blittable structs made only of 2–4 numeric fields (Vector2/3/4, Quaternion, Vector2Int, Rect...).</summary>
    internal static FieldInfo[]? VectorFields(Type type)
    {
        if (!type.IsValueType || type.IsPrimitive || type.IsEnum)
            return null;

        var fields = type.GetFields(BindingFlags.Public | BindingFlags.Instance);
        if (fields.Length < 2 || fields.Length > 4 || fields.Any(f => !IsNumeric(f.FieldType)))
            return null;
        return fields;
    }

    private static object ParseVector(string text, Type target, FieldInfo[] fields)
    {
        var parts = text.Trim('(', ')', '[', ']', ' ').Split(new[] { ',', ' ', ';' }, StringSplitOptions.RemoveEmptyEntries);
        if (parts.Length != fields.Length)
            throw new FormatException($"expected {fields.Length} numbers ({string.Join(", ", fields.Select(f => f.Name))})");

        var boxed = Activator.CreateInstance(target)!;
        for (var i = 0; i < fields.Length; i++)
            fields[i].SetValue(boxed, Parse(parts[i], fields[i].FieldType));
        return boxed;
    }

    /// <summary>Accepts <c>#RRGGBB</c>, <c>#RRGGBBAA</c> or <c>r, g, b[, a]</c> in 0–1 (Color) / 0–255 (Color32).</summary>
    private static object ParseColor(string text, Type target)
    {
        float[] rgba;
        if (text.StartsWith('#'))
        {
            var hex = text.Substring(1);
            if (hex.Length != 6 && hex.Length != 8)
                throw new FormatException("expected #RRGGBB or #RRGGBBAA");
            rgba = new float[4];
            for (var i = 0; i < 4; i++)
                rgba[i] = i * 2 < hex.Length ? int.Parse(hex.Substring(i * 2, 2), NumberStyles.HexNumber) / 255f : 1f;
            if (IsColor32(target))
                rgba = rgba.Select(v => v * 255f).ToArray();
        }
        else
        {
            var parts = text.Trim('(', ')', ' ').Split(new[] { ',', ' ' }, StringSplitOptions.RemoveEmptyEntries);
            if (parts.Length is < 3 or > 4)
                throw new FormatException("expected r, g, b[, a]");
            rgba = parts.Select(p => float.Parse(p, NumberStyles.Float, CultureInfo.InvariantCulture)).ToArray();
            if (rgba.Length == 3)
                rgba = rgba.Append(IsColor32(target) ? 255f : 1f).ToArray();
        }

        var boxed = Activator.CreateInstance(target)!;
        var names = new[] { "r", "g", "b", "a" };
        for (var i = 0; i < 4; i++)
        {
            var field = target.GetField(names[i])!;
            field.SetValue(boxed, System.Convert.ChangeType(field.FieldType == typeof(byte) ? MathF.Round(rgba[i]) : rgba[i], field.FieldType, CultureInfo.InvariantCulture));
        }

        return boxed;
    }
}
