using System.Reflection;
using System.Runtime.CompilerServices;
using Il2CppInterop.Runtime;
using Il2CppInterop.Runtime.InteropTypes;
using Il2CppInterop.Runtime.InteropTypes.Arrays;
using Il2CppInterop.Runtime.Runtime;

namespace Il2cppConsoleMod.Inspector;

/// <summary>
/// Bridges Il2Cpp runtime objects to the managed interop view:
/// resolves the real (most derived) runtime type of a wrapper, unboxes boxed
/// primitives / strings / blittable structs, and tells Il2Cpp fields (exposed by
/// the interop generator as properties) apart from real properties.
///
/// Everything here touches the Il2Cpp runtime and must run on the Unity main thread.
/// Methods are kept out of line so plain .NET objects never JIT Il2Cpp code paths.
/// </summary>
public static class Il2CppSupport
{
    private static readonly Dictionary<IntPtr, Type?> ClassToType = new();
    private static readonly Dictionary<PropertyInfo, bool> NativeFieldCache = new();
    private static readonly MethodInfo UnboxMethod = typeof(Il2CppObjectBase).GetMethod(nameof(Il2CppObjectBase.Unbox))!;
    private static readonly MethodInfo PoolGetMethod = typeof(Il2CppObjectPool).GetMethod(nameof(Il2CppObjectPool.Get))!;

    private static readonly Dictionary<string, Type> Primitives = new()
    {
        ["System.Boolean"] = typeof(bool),
        ["System.Char"] = typeof(char),
        ["System.SByte"] = typeof(sbyte),
        ["System.Byte"] = typeof(byte),
        ["System.Int16"] = typeof(short),
        ["System.UInt16"] = typeof(ushort),
        ["System.Int32"] = typeof(int),
        ["System.UInt32"] = typeof(uint),
        ["System.Int64"] = typeof(long),
        ["System.UInt64"] = typeof(ulong),
        ["System.Single"] = typeof(float),
        ["System.Double"] = typeof(double),
        ["System.IntPtr"] = typeof(IntPtr),
        ["System.UIntPtr"] = typeof(UIntPtr),
        ["System.String"] = typeof(string),
        ["System.Void"] = typeof(void),
    };

    /// <summary>
    /// Returns the value that should be shown for an Il2Cpp wrapper: a managed string,
    /// an unboxed primitive/enum/struct, or a wrapper of the most derived interop type.
    /// </summary>
    [MethodImpl(MethodImplOptions.NoInlining)]
    public static object? Normalize(Il2CppObjectBase obj)
    {
        if (obj.WasCollected)
            return obj;

        var ptr = obj.Pointer;
        if (ptr == IntPtr.Zero)
            return null;

        var klass = IL2CPP.il2cpp_object_get_class(ptr);
        var runtimeType = ResolveClass(klass, ptr);
        if (runtimeType == null || runtimeType == obj.GetType())
            return obj;

        if (runtimeType == typeof(string))
            return IL2CPP.Il2CppStringToManaged(ptr);

        // Boxed primitives, enums and blittable structs come back as plain managed values.
        if (runtimeType.IsValueType)
        {
            try
            {
                return UnboxMethod.MakeGenericMethod(runtimeType).Invoke(obj, null);
            }
            catch
            {
                return obj;
            }
        }

        if (!typeof(Il2CppObjectBase).IsAssignableFrom(runtimeType) || runtimeType.IsAbstract)
            return obj;

        try
        {
            return Wrap(runtimeType, ptr);
        }
        catch
        {
            return obj;
        }
    }

    /// <summary>
    /// Wrapper of <paramref name="type"/> for a native object. Goes through the object pool so
    /// ClassInjector types resolve to their existing managed instance instead of a fresh copy.
    /// </summary>
    [MethodImpl(MethodImplOptions.NoInlining)]
    public static object Wrap(Type type, IntPtr pointer)
    {
        try
        {
            return PoolGetMethod.MakeGenericMethod(type).Invoke(null, new object[] { pointer })!;
        }
        catch
        {
            return Activator.CreateInstance(type, pointer)!;
        }
    }

    /// <summary>Il2Cpp-side full type name, for display when no managed type is available.</summary>
    [MethodImpl(MethodImplOptions.NoInlining)]
    public static string? NativeTypeName(Il2CppObjectBase obj)
    {
        try
        {
            var klass = IL2CPP.il2cpp_object_get_class(obj.Pointer);
            var ns = IL2CPP.il2cpp_class_get_namespace_(klass);
            var name = IL2CPP.il2cpp_class_get_name_(klass);
            return string.IsNullOrEmpty(ns) ? name : ns + "." + name;
        }
        catch
        {
            return null;
        }
    }

    /// <summary>
    /// True when the interop property stands for an Il2Cpp field. The generator emits a
    /// private static <c>NativeFieldInfoPtr_&lt;name&gt;</c> next to every such property.
    /// </summary>
    public static bool IsNativeField(PropertyInfo property)
    {
        lock (NativeFieldCache)
        {
            if (!NativeFieldCache.TryGetValue(property, out var value))
            {
                value = property.DeclaringType?.GetField(
                    "NativeFieldInfoPtr_" + property.Name,
                    BindingFlags.NonPublic | BindingFlags.Static | BindingFlags.DeclaredOnly) != null;
                NativeFieldCache[property] = value;
            }

            return value;
        }
    }

    /// <summary>Calls the Il2Cpp <c>ToString()</c>; null when it only echoes the type name.</summary>
    [MethodImpl(MethodImplOptions.NoInlining)]
    public static string? NativeToString(Il2CppObjectBase obj)
    {
        if (obj is not Il2CppSystem.Object o)
            return null;

        var text = o.ToString();
        if (string.IsNullOrEmpty(text) || text == NativeTypeName(obj))
            return null;
        return text;
    }

    private static Type? ResolveClass(IntPtr klass, IntPtr objectPtr)
    {
        lock (ClassToType)
        {
            if (ClassToType.TryGetValue(klass, out var cached))
                return cached;
        }

        Type? resolved = null;
        try
        {
            var il2cppType = new Il2CppSystem.Object(objectPtr).GetIl2CppType();
            resolved = ToManaged(il2cppType, 0);
        }
        catch
        {
            // leave unresolved; the original wrapper is shown
        }

        lock (ClassToType)
            ClassToType[klass] = resolved;
        return resolved;
    }

    /// <summary>Maps an Il2Cpp reflection type onto the matching interop (managed) type.</summary>
    [MethodImpl(MethodImplOptions.NoInlining)]
    public static Type? ToManaged(Il2CppSystem.Type type, int depth)
    {
        if (depth > 8)
            return null;

        if (type.IsArray)
        {
            var element = ToManaged(type.GetElementType(), depth + 1);
            if (element == null)
                return null;
            if (element == typeof(string))
                return typeof(Il2CppStringArray);
            try
            {
                return element.IsValueType
                    ? typeof(Il2CppStructArray<>).MakeGenericType(element)
                    : typeof(Il2CppReferenceArray<>).MakeGenericType(element);
            }
            catch
            {
                return null;
            }
        }

        if (type.IsGenericType && !type.IsGenericTypeDefinition)
        {
            var definition = ToManaged(type.GetGenericTypeDefinition(), depth + 1);
            if (definition == null)
                return null;

            var nativeArgs = type.GetGenericArguments();
            var args = new Type[nativeArgs.Length];
            for (var i = 0; i < args.Length; i++)
            {
                var arg = ToManaged(nativeArgs[i], depth + 1);
                if (arg == null)
                    return null;
                args[i] = arg == typeof(object) ? typeof(Il2CppSystem.Object) : arg;
            }

            try
            {
                return definition.MakeGenericType(args);
            }
            catch
            {
                return null;
            }
        }

        var name = NestedName(type);
        if (Primitives.TryGetValue(name, out var primitive))
            return primitive;
        if (name == "System.Object")
            return typeof(Il2CppSystem.Object);

        return TypeCatalog.FindInteropType(name);
    }

    /// <summary>"Namespace.Outer+Inner" in Il2Cpp (un-prefixed) naming.</summary>
    private static string NestedName(Il2CppSystem.Type type)
    {
        var chain = new List<string>();
        var outer = type;
        for (var t = type; t != null; t = t.IsNested ? t.DeclaringType : null)
        {
            chain.Insert(0, t.Name);
            outer = t;
        }

        var ns = outer.Namespace;
        var joined = string.Join("+", chain);
        return string.IsNullOrEmpty(ns) ? joined : ns + "." + joined;
    }
}
