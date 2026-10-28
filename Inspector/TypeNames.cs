using System.Text;

namespace Il2cppConsoleMod.Inspector;

/// <summary>Readable type names for the UI, and compilable C# names for rewritten console code.</summary>
public static class TypeNames
{
    private static readonly Dictionary<Type, string> Keywords = new()
    {
        [typeof(void)] = "void",
        [typeof(object)] = "object",
        [typeof(string)] = "string",
        [typeof(bool)] = "bool",
        [typeof(char)] = "char",
        [typeof(byte)] = "byte",
        [typeof(sbyte)] = "sbyte",
        [typeof(short)] = "short",
        [typeof(ushort)] = "ushort",
        [typeof(int)] = "int",
        [typeof(uint)] = "uint",
        [typeof(long)] = "long",
        [typeof(ulong)] = "ulong",
        [typeof(float)] = "float",
        [typeof(double)] = "double",
        [typeof(decimal)] = "decimal",
    };

    /// <summary>Short name such as <c>List&lt;Food&gt;</c> or <c>Outer.Inner</c>.</summary>
    public static string Display(Type type) => Build(type, qualified: false);

    /// <summary>Fully qualified, compilable name such as <c>global::Il2CppSystem.Collections.Generic.List&lt;global::Food&gt;</c>.</summary>
    public static string CSharp(Type type) => Build(type, qualified: true);

    /// <summary>
    /// Closest type that console code can name: walks up base types until a public
    /// one is found. Returns null when only <c>object</c> fits.
    /// </summary>
    public static Type? NameableType(Type type)
    {
        for (var t = type; t != null && t != typeof(object); t = t.BaseType)
        {
            if (t.IsVisible && !t.ContainsGenericParameters && !IsCompilerGenerated(t))
                return t;
        }

        return null;
    }

    public static bool IsCompilerGenerated(Type type)
        => type.Name.Contains('<') || type.Name.StartsWith("__c", StringComparison.Ordinal)
           || type.Name.Contains("DisplayClass", StringComparison.Ordinal);

    private static string Build(Type type, bool qualified)
    {
        if (Keywords.TryGetValue(type, out var keyword))
            return keyword;

        if (type.IsByRef)
            return Build(type.GetElementType()!, qualified);

        if (type.IsPointer)
            return Build(type.GetElementType()!, qualified) + "*";

        if (type.IsArray)
        {
            var rank = type.GetArrayRank();
            return Build(type.GetElementType()!, qualified) + "[" + new string(',', rank - 1) + "]";
        }

        if (type.IsGenericParameter)
            return type.Name;

        var underlying = Nullable.GetUnderlyingType(type);
        if (underlying != null)
            return Build(underlying, qualified) + "?";

        // Generic arguments of nested types are flattened onto the innermost type
        // by reflection, so distribute them back over the declaring chain.
        var args = type.IsGenericType ? type.GetGenericArguments() : Type.EmptyTypes;
        var chain = new List<Type>();
        for (var t = type; t != null; t = t.IsNested ? t.DeclaringType : null)
            chain.Insert(0, t);

        var sb = new StringBuilder();
        if (qualified)
        {
            sb.Append("global::");
            if (!string.IsNullOrEmpty(chain[0].Namespace))
                sb.Append(chain[0].Namespace).Append('.');
        }

        var used = 0;
        for (var i = 0; i < chain.Count; i++)
        {
            if (i > 0)
                sb.Append('.');

            var name = chain[i].Name;
            var tick = name.IndexOf('`');
            var arity = 0;
            if (tick >= 0)
            {
                int.TryParse(name.AsSpan(tick + 1), out arity);
                name = name.Substring(0, tick);
            }

            sb.Append(name);
            if (arity > 0 && used + arity <= args.Length)
            {
                sb.Append('<');
                for (var a = 0; a < arity; a++)
                {
                    if (a > 0)
                        sb.Append(", ");
                    sb.Append(Build(args[used + a], qualified));
                }

                sb.Append('>');
                used += arity;
            }
        }

        return sb.ToString();
    }
}
