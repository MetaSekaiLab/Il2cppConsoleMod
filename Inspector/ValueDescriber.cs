using System.Collections;
using System.Globalization;
using System.Reflection;
using System.Runtime.CompilerServices;
using Il2CppInterop.Runtime.InteropTypes;

namespace Il2cppConsoleMod.Inspector;

/// <summary>Compact preview of a value; anything that can be drilled into carries a handle.</summary>
public sealed class ValueDto
{
    /// <summary>null | str | num | bool | char | enum | ptr | type | obj | struct | destroyed | err | void</summary>
    public string K { get; set; } = "obj";
    public string? T { get; set; }
    public string? V { get; set; }
    public int? H { get; set; }
    public int? N { get; set; }
    public float[]? C { get; set; }
    public bool? Trunc { get; set; }
}

public static class ValueDescriber
{
    public const int PreviewLength = 300;

    private static readonly Dictionary<Type, bool> CollectionCache = new();
    private static readonly Dictionary<Type, PropertyInfo?> CountCache = new();

    public static ValueDto Null => new() { K = "null" };

    public static ValueDto Error(string message) => new() { K = "err", V = message };

    public static ValueDto Describe(object? value)
    {
        if (value == null)
            return Null;

        if (value is Il2CppObjectBase native)
        {
            if (native.WasCollected)
                return new ValueDto { K = "err", T = TypeNames.Display(value.GetType()), V = "collected by Il2Cpp GC" };

            value = Il2CppSupport.Normalize(native);
            if (value == null)
                return Null;
        }

        switch (value)
        {
            case string s:
                return DescribeString(s);
            case bool b:
                return new ValueDto { K = "bool", V = b ? "true" : "false" };
            case char c:
                return new ValueDto { K = "char", V = c.ToString() };
            case Enum e:
                return new ValueDto { K = "enum", T = TypeNames.Display(e.GetType()), V = e.ToString() };
            case IntPtr p:
                return new ValueDto { K = "ptr", T = "IntPtr", V = "0x" + p.ToString("X") };
            case UIntPtr up:
                return new ValueDto { K = "ptr", T = "UIntPtr", V = "0x" + up.ToUInt64().ToString("X") };
            case Type t:
                return new ValueDto { K = "type", T = "Type", V = TypeNames.Display(t), H = ObjectRegistry.Register(StaticTypeRef.For(t)) };
            case StaticTypeRef r:
                return new ValueDto { K = "type", T = "Type", V = TypeNames.Display(r.Type), H = ObjectRegistry.Register(r) };
        }

        var type = value.GetType();
        if (type.IsPrimitive || value is decimal)
            return new ValueDto { K = "num", T = TypeNames.Display(type), V = Convert.ToString(value, CultureInfo.InvariantCulture) };

        if (type.IsValueType)
        {
            return new ValueDto
            {
                K = "struct",
                T = TypeNames.Display(type),
                V = VectorText(value, type) ?? SafeToString(value, type),
                C = ColorOf(value, type),
                H = ObjectRegistry.Register(value),
            };
        }

        if (value is Il2CppObjectBase il2cpp)
            return DescribeIl2Cpp(il2cpp, type);

        return new ValueDto
        {
            K = "obj",
            T = TypeNames.Display(type),
            V = value is Delegate d ? d.Method.Name : SafeToString(value, type),
            N = CountOf(value),
            H = ObjectRegistry.Register(value),
        };
    }

    /// <summary>
    /// Collections expose items to the UI. Managed enumerables qualify, as do Il2Cpp
    /// types with a parameterless GetEnumerator (List, Dictionary, HashSet, Transform...).
    /// </summary>
    public static bool IsCollection(Type type)
    {
        if (type == typeof(string) || type == typeof(StaticTypeRef))
            return false;

        lock (CollectionCache)
        {
            if (CollectionCache.TryGetValue(type, out var cached))
                return cached;
        }

        var result = typeof(IEnumerable).IsAssignableFrom(type);
        if (!result && typeof(Il2CppObjectBase).IsAssignableFrom(type) && type.FullName != "Il2CppSystem.String")
            result = type.GetMethod("GetEnumerator", BindingFlags.Public | BindingFlags.Instance, Type.EmptyTypes) != null;

        lock (CollectionCache)
            CollectionCache[type] = result;
        return result;
    }

    public static int? CountOf(object value)
    {
        try
        {
            switch (value)
            {
                case string:
                    return null;
                case Array array:
                    return array.Length;
                case ICollection collection:
                    return collection.Count;
            }

            var type = value.GetType();
            if (!IsCollection(type))
                return null;

            var property = CountProperty(type);
            return property?.GetValue(value) is int count ? count : null;
        }
        catch
        {
            return null;
        }
    }

    public static PropertyInfo? CountProperty(Type type)
    {
        lock (CountCache)
        {
            if (!CountCache.TryGetValue(type, out var property))
            {
                property = FindIntProperty(type, "Count") ?? FindIntProperty(type, "Length");
                CountCache[type] = property;
            }

            return property;
        }
    }

    public static string? SafeToString(object value, Type type)
    {
        try
        {
            var method = type.GetMethod("ToString", BindingFlags.Public | BindingFlags.Instance, Type.EmptyTypes);
            if (method == null || method.DeclaringType == typeof(object) || method.DeclaringType == typeof(ValueType))
                return null;

            return Truncate(value.ToString());
        }
        catch (Exception ex)
        {
            return "<ToString threw " + Unwrap(ex).GetType().Name + ">";
        }
    }

    public static string Truncate(string? text, int max = PreviewLength)
    {
        if (text == null)
            return "";
        return text.Length <= max ? text : text.Substring(0, max) + "…";
    }

    public static Exception Unwrap(Exception ex)
    {
        while (ex is TargetInvocationException { InnerException: { } inner })
            ex = inner;
        return ex;
    }

    /// <summary>First line of an exception message, Il2Cpp stack traces stripped.</summary>
    public static string Message(Exception ex)
    {
        ex = Unwrap(ex);
        var text = ex.Message;
        var newline = text.IndexOf('\n');
        if (newline > 0)
            text = text.Substring(0, newline).TrimEnd('\r');
        return ex.GetType().Name + ": " + Truncate(text, 400);
    }

    private static ValueDto DescribeString(string s)
    {
        var dto = new ValueDto { K = "str", V = s.Length > PreviewLength ? s.Substring(0, PreviewLength) : s, N = s.Length };
        if (s.Length > PreviewLength)
        {
            dto.Trunc = true;
            dto.H = ObjectRegistry.Register(s);
        }

        return dto;
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    private static ValueDto DescribeIl2Cpp(Il2CppObjectBase value, Type type)
    {
        var dto = new ValueDto { K = "obj", T = TypeNames.Display(type) };
        if (value is UnityEngine.Object unity)
        {
            if (unity == null)
            {
                dto.K = "destroyed";
                dto.V = "destroyed";
                return dto;
            }

            try
            {
                dto.V = unity.name;
            }
            catch
            {
                // some native objects refuse name lookups; fall through to the type name
            }
        }
        else
        {
            try
            {
                dto.V = Truncate(Il2CppSupport.NativeToString(value));
            }
            catch (Exception ex)
            {
                dto.V = "<ToString threw " + Unwrap(ex).GetType().Name + ">";
            }

            dto.N = CountOf(value);
        }

        dto.H = ObjectRegistry.Register(value);
        return dto;
    }

    /// <summary>
    /// Full-precision "(x, y, z)" for vector-like structs; Unity's own ToString rounds to two
    /// decimals, which would also make inline edits lossy.
    /// </summary>
    private static string? VectorText(object value, Type type)
    {
        var fields = ValueConverter.IsColor(type) ? type.GetFields(BindingFlags.Public | BindingFlags.Instance) : ValueConverter.VectorFields(type);
        if (fields == null)
            return null;

        try
        {
            return "(" + string.Join(", ", fields.Select(f => Convert.ToString(f.GetValue(value), CultureInfo.InvariantCulture))) + ")";
        }
        catch
        {
            return null;
        }
    }

    private static float[]? ColorOf(object value, Type type)
    {
        try
        {
            if (!ValueConverter.IsColor(type))
                return null;
            var scale = ValueConverter.IsColor32(type) ? 255f : 1f;
            return new[] { F(value, type, "r") / scale, F(value, type, "g") / scale, F(value, type, "b") / scale, F(value, type, "a") / scale };
        }
        catch
        {
            // not a color after all
        }

        return null;
    }

    private static float F(object value, Type type, string field)
        => Convert.ToSingle(type.GetField(field)!.GetValue(value), CultureInfo.InvariantCulture);

    private static PropertyInfo? FindIntProperty(Type type, string name)
    {
        try
        {
            var property = type.GetProperty(name, BindingFlags.Public | BindingFlags.Instance);
            return property != null && property.PropertyType == typeof(int) && property.GetIndexParameters().Length == 0 ? property : null;
        }
        catch (AmbiguousMatchException)
        {
            return type.GetProperties(BindingFlags.Public | BindingFlags.Instance)
                .FirstOrDefault(p => p.Name == name && p.PropertyType == typeof(int) && p.GetIndexParameters().Length == 0);
        }
    }
}
