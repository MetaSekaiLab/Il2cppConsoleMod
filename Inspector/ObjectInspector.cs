using System.Collections;
using System.Globalization;
using System.Reflection;
using System.Text.Json;
using Il2CppInterop.Runtime.InteropTypes;

namespace Il2cppConsoleMod.Inspector;

public sealed class MemberDto
{
    public string Name { get; set; } = "";
    /// <summary>field | prop | const</summary>
    public string Kind { get; set; } = "field";
    /// <summary>True for Il2Cpp fields (exposed by the interop generator as properties).</summary>
    public bool? Native { get; set; }
    public string Type { get; set; } = "";
    public string Decl { get; set; } = "";
    public int Depth { get; set; }
    public bool? Static { get; set; }
    public bool? CanWrite { get; set; }
    /// <summary>Inline editor: bool | num | str | char | enum | flags | color | vec</summary>
    public string? Edit { get; set; }
    public string[]? Options { get; set; }
    public ValueDto? Value { get; set; }
    /// <summary>Not read automatically; the UI offers a button. <see cref="LazyHint"/> says why when it matters.</summary>
    public bool? Lazy { get; set; }
    public string? LazyHint { get; set; }
    public string? Error { get; set; }
}

public sealed class ItemDto
{
    public int I { get; set; }
    public ValueDto? Key { get; set; }
    public ValueDto Value { get; set; } = ValueDescriber.Null;
}

public sealed class ItemsDto
{
    public int? Count { get; set; }
    public int Offset { get; set; }
    public List<ItemDto> Items { get; set; } = new();
    public bool More { get; set; }
}

public sealed class ParamDto
{
    public string Name { get; set; } = "";
    public string Type { get; set; } = "";
    public string? Edit { get; set; }
    public string[]? Options { get; set; }
    public bool? Optional { get; set; }
    public string? Default { get; set; }
}

public sealed class MethodDto
{
    public int Id { get; set; }
    public string Name { get; set; } = "";
    public string Ret { get; set; } = "";
    public List<ParamDto> Params { get; set; } = new();
    public bool? Static { get; set; }
    public string Decl { get; set; } = "";
    public int Depth { get; set; }
    public bool Ok { get; set; }
}

/// <summary>Reflection over live objects: members, collection items, methods, edits and calls.</summary>
public static class ObjectInspector
{
    private const BindingFlags Declared = BindingFlags.Public | BindingFlags.DeclaredOnly;

    /// <summary>Getters with side effects (instantiate a material copy, advance the RNG...), only read one by one on request.</summary>
    private static readonly HashSet<string> LazyProperties = new(StringComparer.Ordinal)
    {
        "UnityEngine.Renderer.material",
        "UnityEngine.Renderer.materials",
        "UnityEngine.MeshFilter.mesh",
        "UnityEngine.Collider.material",
        "UnityEngine.Collider2D.sharedMaterial",
        "UnityEngine.UI.Graphic.materialForRendering",
        "TMPro.TMP_Text.fontMaterial",
        "TMPro.TMP_Text.fontMaterials",
        "UnityEngine.Random.value",
        "UnityEngine.Random.insideUnitCircle",
        "UnityEngine.Random.insideUnitSphere",
        "UnityEngine.Random.onUnitSphere",
        "UnityEngine.Random.rotation",
        "UnityEngine.Random.rotationUniform",
    };

    /// <summary>Obsolete Unity shortcut accessors that always throw.</summary>
    private static readonly HashSet<string> HiddenUnityShortcuts = new(StringComparer.Ordinal)
    {
        "rigidbody", "rigidbody2D", "camera", "light", "animation", "constantForce", "renderer", "audio",
        "networkView", "collider", "collider2D", "hingeJoint", "particleSystem", "guiText", "guiTexture", "guiElement",
    };

    private static readonly Dictionary<(Type, bool), List<MethodInfo>> MethodCache = new();

    public static (Type type, object? instance) Target(object target)
        => target is StaticTypeRef r ? (r.Type, null) : (target.GetType(), target);

    /// <param name="readStaticProperties">
    /// Also read static properties. They stay lazy by default: static getters such as
    /// singleton <c>Instance</c> accessors may create the object they return. Il2Cpp static
    /// fields are plain reads and are always shown.
    /// </param>
    /// <param name="only">Read just this member (side-effect getters included), skip the rest.</param>
    public static List<MemberDto> GetMembers(object target, bool readStaticProperties = false, string? only = null)
    {
        var (type, instance) = Target(target);
        var statics = instance == null;
        var members = new List<MemberDto>();
        var seen = new HashSet<string>(StringComparer.Ordinal);

        if (statics && type.IsEnum)
            return EnumConstants(type);

        var flags = Declared | (statics ? BindingFlags.Static : BindingFlags.Instance);
        var depth = 0;
        for (var t = type; t != null && !IsStopType(t); t = t.BaseType, depth++)
        {
            var decl = TypeNames.Display(t);

            foreach (var field in SafeFields(t, flags))
            {
                if (only != null && field.Name != only || !seen.Add(field.Name) || field.Name.Contains('<'))
                    continue;

                var dto = new MemberDto
                {
                    Name = field.Name,
                    Kind = field.IsLiteral ? "const" : "field",
                    Type = TypeNames.Display(field.FieldType),
                    Decl = decl,
                    Depth = depth,
                    Static = statics ? true : null,
                };
                FillEditor(dto, field.FieldType, !field.IsLiteral && !field.IsInitOnly && !(instance?.GetType().IsValueType ?? false));
                try
                {
                    dto.Value = ValueDescriber.Describe(field.IsLiteral ? field.GetRawConstantValue() : field.GetValue(instance));
                }
                catch (Exception ex)
                {
                    dto.Error = ValueDescriber.Message(ex);
                }

                members.Add(dto);
            }

            foreach (var property in SafeProperties(t, flags))
            {
                if (only != null && property.Name != only)
                    continue;
                if (property.GetIndexParameters().Length > 0 || property.GetMethod?.IsPublic != true || !seen.Add(property.Name))
                    continue;
                if (IsHidden(t, property))
                    continue;

                var native = Il2CppSupport.IsNativeField(property);
                var dto = new MemberDto
                {
                    Name = property.Name,
                    Kind = native ? "field" : "prop",
                    Native = native ? true : null,
                    Type = TypeNames.Display(property.PropertyType),
                    Decl = decl,
                    Depth = depth,
                    Static = statics ? true : null,
                };
                FillEditor(dto, property.PropertyType, property.SetMethod?.IsPublic == true && !(instance?.GetType().IsValueType ?? false));

                var sideEffect = LazyProperties.Contains(t.FullName + "." + property.Name);
                if (only == null && (sideEffect || statics && !native && !readStaticProperties))
                {
                    dto.Lazy = true;
                    dto.LazyHint = sideEffect ? "side-effect" : null;
                }
                else
                {
                    try
                    {
                        dto.Value = ValueDescriber.Describe(property.GetValue(instance));
                    }
                    catch (Exception ex)
                    {
                        dto.Error = ValueDescriber.Message(ex);
                    }
                }

                members.Add(dto);
            }
        }

        return members;
    }

    /// <summary>Reads a single member, also the ones that are lazy by default.</summary>
    public static MemberDto GetMember(object target, string name)
        => GetMembers(target, only: name).FirstOrDefault() ?? throw new MissingMemberException("no member named " + name);

    public static List<string> BaseChain(Type type)
    {
        var chain = new List<string>();
        for (var t = type; t != null && !IsStopType(t); t = t.BaseType)
            chain.Add(TypeNames.Display(t));
        return chain;
    }

    // ---- collections -------------------------------------------------------------------------

    public static ItemsDto GetItems(object target, int offset, int limit)
    {
        offset = Math.Max(0, offset);
        limit = Math.Clamp(limit, 1, 1000);
        var result = new ItemsDto { Offset = offset, Count = ValueDescriber.CountOf(target) };

        switch (target)
        {
            case Array { Rank: 1 } array:
                for (var i = offset; i < array.Length && i < offset + limit; i++)
                    result.Items.Add(Item(i, null, () => array.GetValue(i)));
                result.More = offset + limit < array.Length;
                return result;
            case IDictionary dictionary:
                return Enumerate(result, dictionary.GetEnumerator(), offset, limit);
            case IList list:
                for (var i = offset; i < list.Count && i < offset + limit; i++)
                    result.Items.Add(Item(i, null, () => list[i]));
                result.More = offset + limit < list.Count;
                return result;
        }

        // Il2CppArrayBase<T> and Il2Cpp List<T>: random access, so paging stays cheap.
        var type = target.GetType();
        var indexer = IntIndexer(type);
        if (indexer != null && result.Count is { } count)
        {
            for (var i = offset; i < count && i < offset + limit; i++)
            {
                var index = i;
                result.Items.Add(Item(i, null, () => indexer.GetValue(target, new object[] { index })));
            }

            result.More = offset + limit < count;
            return result;
        }

        if (target is IEnumerable enumerable)
            return Enumerate(result, enumerable.GetEnumerator(), offset, limit);

        var getEnumerator = type.GetMethod("GetEnumerator", BindingFlags.Public | BindingFlags.Instance, Type.EmptyTypes)
                            ?? throw new InvalidOperationException(TypeNames.Display(type) + " is not a collection");
        var enumerator = getEnumerator.Invoke(target, null) ?? throw new InvalidOperationException("GetEnumerator returned null");
        return Enumerate(result, new ReflectionEnumerator(enumerator), offset, limit);
    }

    private static ItemsDto Enumerate(ItemsDto result, IEnumerator enumerator, int offset, int limit)
    {
        try
        {
            var i = 0;
            while (i < offset && enumerator.MoveNext())
                i++;

            while (i < offset + limit && enumerator.MoveNext())
            {
                var current = enumerator.Current;
                result.Items.Add(KeyValue(i, current));
                i++;
            }

            result.More = i == offset + limit && enumerator.MoveNext();
        }
        finally
        {
            (enumerator as IDisposable)?.Dispose();
        }

        return result;
    }

    private static ItemDto KeyValue(int index, object? current)
    {
        if (current is DictionaryEntry entry)
            return new ItemDto { I = index, Key = ValueDescriber.Describe(entry.Key), Value = ValueDescriber.Describe(entry.Value) };

        if (current != null)
        {
            var type = current.GetType();
            // Managed KeyValuePair<,> and the Il2Cpp KeyValuePair`2 wrapper share these names.
            if (type.Name.StartsWith("KeyValuePair`2", StringComparison.Ordinal))
            {
                var key = type.GetProperty("Key");
                var value = type.GetProperty("Value");
                if (key != null && value != null)
                {
                    var dto = new ItemDto { I = index };
                    try
                    {
                        dto.Key = ValueDescriber.Describe(key.GetValue(current));
                        dto.Value = ValueDescriber.Describe(value.GetValue(current));
                    }
                    catch (Exception ex)
                    {
                        dto.Value = ValueDescriber.Error(ValueDescriber.Message(ex));
                    }

                    return dto;
                }
            }
        }

        return new ItemDto { I = index, Value = ValueDescriber.Describe(current) };
    }

    private static ItemDto Item(int index, ValueDto? key, Func<object?> read)
    {
        try
        {
            return new ItemDto { I = index, Key = key, Value = ValueDescriber.Describe(read()) };
        }
        catch (Exception ex)
        {
            return new ItemDto { I = index, Key = key, Value = ValueDescriber.Error(ValueDescriber.Message(ex)) };
        }
    }

    /// <summary><c>this[int]</c> for list-like types; dictionaries keyed by int are excluded.</summary>
    private static PropertyInfo? IntIndexer(Type type)
    {
        if (type.GetMethod("ContainsKey", BindingFlags.Public | BindingFlags.Instance) != null)
            return null;

        return type.GetProperties(BindingFlags.Public | BindingFlags.Instance).FirstOrDefault(p =>
        {
            var parameters = p.GetIndexParameters();
            return parameters.Length == 1 && parameters[0].ParameterType == typeof(int) && p.GetMethod?.IsPublic == true;
        });
    }

    /// <summary>Drives an Il2Cpp enumerator (MoveNext / Current) through reflection.</summary>
    private sealed class ReflectionEnumerator : IEnumerator
    {
        private readonly object _inner;
        private readonly MethodInfo _moveNext;
        private readonly PropertyInfo _current;

        public ReflectionEnumerator(object inner)
        {
            _inner = inner;
            var type = inner.GetType();
            _moveNext = type.GetMethod("MoveNext", BindingFlags.Public | BindingFlags.Instance, Type.EmptyTypes)
                        ?? throw new InvalidOperationException("enumerator has no MoveNext");
            _current = type.GetProperties(BindingFlags.Public | BindingFlags.Instance)
                           .FirstOrDefault(p => p.Name == "Current" && p.GetIndexParameters().Length == 0)
                       ?? throw new InvalidOperationException("enumerator has no Current");
        }

        public object? Current => _current.GetValue(_inner);

        public bool MoveNext() => (bool)_moveNext.Invoke(_inner, null)!;

        public void Reset() => throw new NotSupportedException();
    }

    // ---- methods -----------------------------------------------------------------------------

    public static List<MethodDto> GetMethods(object target)
    {
        var (type, instance) = Target(target);
        var list = Methods(type, instance == null);
        var result = new List<MethodDto>(list.Count);
        for (var i = 0; i < list.Count; i++)
        {
            var method = list[i];
            var dto = new MethodDto
            {
                Id = i,
                Name = method.Name,
                Ret = TypeNames.Display(method.ReturnType),
                Static = method.IsStatic ? true : null,
                Decl = TypeNames.Display(method.DeclaringType!),
                Depth = Depth(type, method.DeclaringType!),
                Ok = !method.ContainsGenericParameters,
            };

            foreach (var parameter in method.GetParameters())
            {
                var p = new ParamDto
                {
                    Name = parameter.Name ?? "arg" + parameter.Position,
                    Type = (parameter.IsOut ? "out " : parameter.ParameterType.IsByRef ? "ref " : "") + TypeNames.Display(parameter.ParameterType),
                };
                var parameterType = parameter.ParameterType.IsByRef ? parameter.ParameterType.GetElementType()! : parameter.ParameterType;
                var probe = new MemberDto();
                FillEditor(probe, parameterType, true);
                p.Edit = probe.Edit;
                p.Options = probe.Options;
                if (parameter.HasDefaultValue)
                {
                    p.Optional = true;
                    p.Default = parameter.DefaultValue == null ? "null" : Convert.ToString(parameter.DefaultValue, CultureInfo.InvariantCulture);
                }

                if (parameter.ParameterType.IsByRef || parameter.ParameterType.IsPointer)
                    dto.Ok = false;
                dto.Params.Add(p);
            }

            result.Add(dto);
        }

        return result;
    }

    public static object? Invoke(object target, int methodId, JsonElement[] args, out bool isVoid)
    {
        var (type, instance) = Target(target);
        var list = Methods(type, instance == null);
        if (methodId < 0 || methodId >= list.Count)
            throw new ArgumentOutOfRangeException(nameof(methodId), "unknown method id " + methodId);

        var method = list[methodId];
        var parameters = method.GetParameters();
        var values = new object?[parameters.Length];
        for (var i = 0; i < parameters.Length; i++)
        {
            // {"default": true} asks for the declared default of an optional parameter.
            var useDefault = i >= args.Length || args[i].ValueKind == JsonValueKind.Undefined
                             || args[i].ValueKind == JsonValueKind.Object && args[i].TryGetProperty("default", out _);
            if (!useDefault)
                values[i] = ValueConverter.Convert(args[i], parameters[i].ParameterType);
            else if (parameters[i].HasDefaultValue)
                values[i] = parameters[i].DefaultValue;
            else
                throw new ArgumentException("missing argument " + parameters[i].Name);
        }

        isVoid = method.ReturnType == typeof(void);
        return method.Invoke(instance, values);
    }

    private static List<MethodInfo> Methods(Type type, bool statics)
    {
        lock (MethodCache)
        {
            if (MethodCache.TryGetValue((type, statics), out var cached))
                return cached;
        }

        var flags = BindingFlags.Public | (statics ? BindingFlags.Static | BindingFlags.FlattenHierarchy : BindingFlags.Instance);
        var list = type.GetMethods(flags)
            .Where(m => !m.IsSpecialName && m.DeclaringType != null && !IsStopType(m.DeclaringType))
            .Where(m => !m.Name.StartsWith("System_", StringComparison.Ordinal) && !m.Name.StartsWith("Il2CppSystem_", StringComparison.Ordinal))
            .Where(m => !m.Name.Contains('<'))
            .OrderBy(m => Depth(type, m.DeclaringType!))
            .ThenBy(m => m.Name, StringComparer.Ordinal)
            .ThenBy(m => m.GetParameters().Length)
            .ToList();

        lock (MethodCache)
            MethodCache[(type, statics)] = list;
        return list;
    }

    // ---- edits -------------------------------------------------------------------------------

    public static MemberDto SetMember(object target, string name, JsonElement value)
    {
        var (type, instance) = Target(target);
        var flags = BindingFlags.Public | (instance == null ? BindingFlags.Static | BindingFlags.FlattenHierarchy : BindingFlags.Instance);

        var field = type.GetField(name, flags);
        if (field != null)
        {
            if (field.IsLiteral || field.IsInitOnly)
                throw new InvalidOperationException(name + " is read-only");
            field.SetValue(instance, ValueConverter.Convert(value, field.FieldType));
            return GetMember(target, name);
        }

        var property = type.GetProperties(flags).FirstOrDefault(p => p.Name == name && p.GetIndexParameters().Length == 0)
                       ?? throw new MissingMemberException("no member named " + name);
        if (property.SetMethod?.IsPublic != true)
            throw new InvalidOperationException(name + " is read-only");

        property.SetValue(instance, ValueConverter.Convert(value, property.PropertyType));
        return GetMember(target, name);
    }

    // ---- helpers -----------------------------------------------------------------------------

    public static bool IsStopType(Type type)
        => type == typeof(object) || type == typeof(ValueType) || type == typeof(Enum) || type == typeof(Il2CppObjectBase)
           || type.FullName is "Il2CppSystem.Object" or "Il2CppSystem.ValueType" or "Il2CppSystem.Enum";

    private static int Depth(Type type, Type declaring)
    {
        var depth = 0;
        for (var t = type; t != null && t != declaring; t = t.BaseType)
            depth++;
        return depth;
    }

    private static bool IsHidden(Type declaring, PropertyInfo property)
        => HiddenUnityShortcuts.Contains(property.Name)
           && declaring.FullName is "UnityEngine.Component" or "UnityEngine.GameObject";

    private static List<MemberDto> EnumConstants(Type type)
    {
        var underlying = Enum.GetUnderlyingType(type);
        return type.GetFields(BindingFlags.Public | BindingFlags.Static)
            .Select(f => new MemberDto
            {
                Name = f.Name,
                Kind = "const",
                Type = TypeNames.Display(underlying),
                Decl = TypeNames.Display(type),
                Static = true,
                Value = ValueDescriber.Describe(System.Convert.ChangeType(f.GetRawConstantValue(), underlying, CultureInfo.InvariantCulture)),
            })
            .ToList();
    }

    private static IEnumerable<FieldInfo> SafeFields(Type type, BindingFlags flags)
    {
        try
        {
            return type.GetFields(flags);
        }
        catch
        {
            return Array.Empty<FieldInfo>();
        }
    }

    private static IEnumerable<PropertyInfo> SafeProperties(Type type, BindingFlags flags)
    {
        try
        {
            return type.GetProperties(flags);
        }
        catch
        {
            return Array.Empty<PropertyInfo>();
        }
    }

    internal static void FillEditor(MemberDto dto, Type type, bool writable)
    {
        var editor = ValueConverter.EditorFor(type, out var options);
        if (editor == null)
            return;

        dto.Edit = editor;
        dto.Options = options;
        dto.CanWrite = writable ? true : null;
    }
}
