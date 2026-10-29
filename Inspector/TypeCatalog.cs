using System.Reflection;

namespace Il2cppConsoleMod.Inspector;

/// <summary>
/// Snapshot of every public type in the loaded assemblies, for the type browser,
/// type search and Il2Cpp → interop name lookup. Pure reflection metadata, so it is
/// safe to build and query off the Unity main thread. Rebuilt when assemblies load.
/// </summary>
public static class TypeCatalog
{
    public sealed class TypeEntry
    {
        public Type Type { get; init; } = null!;
        public string Name { get; init; } = null!;
        public string FullName { get; init; } = null!;
        public string Namespace { get; init; } = null!;
        public string Assembly { get; init; } = null!;
        public string Kind { get; init; } = null!;
        public bool Native { get; init; }
        public bool Unity { get; init; }
        public int Rank { get; init; }
    }

    public sealed class AssemblyEntry
    {
        public string Name { get; init; } = null!;
        public int Types { get; init; }
        public bool Native { get; init; }
        public string Group { get; init; } = null!;
        public int Rank { get; init; }
    }

    private sealed class Snapshot
    {
        public int AssemblyCount { get; init; }
        public List<TypeEntry> Types { get; init; } = null!;
        public List<AssemblyEntry> Assemblies { get; init; } = null!;
        public Dictionary<string, Type> InteropByName { get; init; } = null!;
    }

    private static readonly object Sync = new();
    private static Snapshot? _snapshot;
    private static bool _rebuilding;

    /// <summary>Builds the catalog in the background so the first lookup does not stall a frame.</summary>
    public static void Prewarm() => ThreadPool.QueueUserWorkItem(_ =>
    {
        try
        {
            GetSnapshot();
        }
        catch
        {
            // built lazily on first use instead
        }
    });

    public static List<AssemblyEntry> Assemblies() => GetSnapshot().Assemblies;

    public static List<TypeEntry> TypesIn(string assembly)
        => GetSnapshot().Types.Where(t => t.Assembly == assembly).ToList();

    public static Type? FindInteropType(string il2cppFullName)
        => GetSnapshot().InteropByName.TryGetValue(il2cppFullName, out var type) ? type : null;

    public static Type? Resolve(string fullName, string? assembly = null)
    {
        foreach (var entry in GetSnapshot().Types)
        {
            if (entry.FullName == fullName && (assembly == null || entry.Assembly == assembly))
                return entry.Type;
        }

        return null;
    }

    /// <summary>
    /// Ranked name search: exact, prefix, word-start initials, substring, subsequence.
    /// Game assemblies sort first.
    /// </summary>
    public static List<TypeEntry> Search(string query, int limit)
    {
        query = query.Trim();
        if (query.Length == 0)
            return new List<TypeEntry>();

        var scored = new List<(int score, TypeEntry entry)>();
        foreach (var entry in GetSnapshot().Types)
        {
            var score = Score(entry, query);
            if (score > 0)
                scored.Add((score - entry.Rank * 3, entry));
        }

        return scored
            .OrderByDescending(s => s.score)
            .ThenBy(s => s.entry.Name.Length)
            .ThenBy(s => s.entry.FullName, StringComparer.Ordinal)
            .Take(limit)
            .Select(s => s.entry)
            .ToList();
    }

    private static int Score(TypeEntry entry, string query)
    {
        var name = entry.Name;
        if (query.Contains('.'))
        {
            if (entry.FullName.Equals(query, StringComparison.OrdinalIgnoreCase))
                return 100;
            return entry.FullName.Contains(query, StringComparison.OrdinalIgnoreCase) ? 50 : 0;
        }

        if (name.Equals(query, StringComparison.Ordinal))
            return 100;
        if (name.Equals(query, StringComparison.OrdinalIgnoreCase))
            return 95;
        if (name.StartsWith(query, StringComparison.OrdinalIgnoreCase))
            return 80;
        var index = name.IndexOf(query, StringComparison.OrdinalIgnoreCase);
        if (index >= 0)
            return 60 - Math.Min(index, 20);
        if (IsHumpMatch(name, query))
            return 70;
        return IsSubsequence(name, query) ? 30 : 0;
    }

    /// <summary>Query letters hit word starts in order.</summary>
    private static bool IsHumpMatch(string name, string query)
    {
        var q = 0;
        for (var i = 0; i < name.Length && q < query.Length; i++)
        {
            var start = i == 0 || char.IsUpper(name[i]) && !char.IsUpper(name[i - 1]) || name[i - 1] is '.' or '_';
            if (start && char.ToLowerInvariant(name[i]) == char.ToLowerInvariant(query[q]))
                q++;
        }

        return q == query.Length;
    }

    private static bool IsSubsequence(string text, string query)
    {
        var q = 0;
        for (var i = 0; i < text.Length && q < query.Length; i++)
        {
            if (char.ToLowerInvariant(text[i]) == char.ToLowerInvariant(query[q]))
                q++;
        }

        return q == query.Length;
    }

    /// <summary>
    /// The first build is synchronous; later ones (an assembly was loaded since) run in the
    /// background while the previous snapshot keeps answering, so lookups made on the Unity
    /// main thread never wait for a full rebuild.
    /// </summary>
    private static Snapshot GetSnapshot()
    {
        var assemblies = LoadedAssemblies();
        lock (Sync)
        {
            if (_snapshot == null)
                return _snapshot = Build(assemblies);

            if (_snapshot.AssemblyCount != assemblies.Length && !_rebuilding)
            {
                _rebuilding = true;
                ThreadPool.QueueUserWorkItem(_ =>
                {
                    try
                    {
                        var fresh = Build(LoadedAssemblies());
                        lock (Sync)
                            _snapshot = fresh;
                    }
                    catch
                    {
                        // keep serving the previous snapshot
                    }
                    finally
                    {
                        lock (Sync)
                            _rebuilding = false;
                    }
                });
            }

            return _snapshot;
        }
    }

    // Console submissions load a new assembly each time; they never add browsable types.
    private static Assembly[] LoadedAssemblies()
        => AppDomain.CurrentDomain.GetAssemblies().Where(a => !a.IsDynamic && !IsTransient(a)).ToArray();

    private static Snapshot Build(Assembly[] assemblies)
    {
        var types = new List<TypeEntry>();
        var asmEntries = new List<AssemblyEntry>();
        var interop = new Dictionary<string, Type>(StringComparer.Ordinal);

        foreach (var asm in assemblies)
        {
            var asmName = asm.GetName().Name ?? "?";
            var native = IsInteropAssembly(asm);
            var (group, rank) = Classify(asmName, native);
            var count = 0;

            foreach (var type in LoadTypes(asm))
            {
                try
                {
                    if (native && !type.IsGenericParameter)
                        IndexInterop(interop, type);

                    if (!(type.IsPublic || type.IsNestedPublic) || TypeNames.IsCompilerGenerated(type))
                        continue;
                    if (type.Name.StartsWith("_PrivateImplementationDetails_", StringComparison.Ordinal))
                        continue;

                    types.Add(new TypeEntry
                    {
                        Type = type,
                        Name = TypeNames.Display(type),
                        FullName = type.FullName ?? type.Name,
                        Namespace = OuterNamespace(type),
                        Assembly = asmName,
                        Kind = KindOf(type),
                        Native = native,
                        Unity = IsUnityObject(type),
                        Rank = rank,
                    });
                    count++;
                }
                catch
                {
                    // broken metadata in a single type should not hide the rest
                }
            }

            if (count > 0)
                asmEntries.Add(new AssemblyEntry { Name = asmName, Types = count, Native = native, Group = group, Rank = rank });
        }

        return new Snapshot
        {
            AssemblyCount = assemblies.Length,
            Types = types,
            Assemblies = asmEntries.OrderBy(a => a.Rank).ThenBy(a => a.Name, StringComparer.OrdinalIgnoreCase).ToList(),
            InteropByName = interop,
        };
    }

    private static bool IsTransient(Assembly asm)
    {
        var name = asm.FullName ?? "";
        return name.StartsWith("DynamicPayload_", StringComparison.Ordinal) || name.StartsWith("ℛ", StringComparison.Ordinal);
    }

    private static void IndexInterop(Dictionary<string, Type> index, Type type)
    {
        var fullName = type.FullName;
        if (fullName == null)
            return;

        index.TryAdd(fullName, type);
        // Il2CppInterop prefixes BCL namespaces: Il2CppSystem.*, Il2CppMono.*, Il2CppMicrosoft.* ...
        if (fullName.StartsWith("Il2Cpp", StringComparison.Ordinal) && type.Namespace?.StartsWith("Il2Cpp", StringComparison.Ordinal) == true)
            index.TryAdd(fullName.Substring("Il2Cpp".Length), type);
    }

    private static IEnumerable<Type> LoadTypes(Assembly asm)
    {
        try
        {
            return asm.GetTypes();
        }
        catch (ReflectionTypeLoadException ex)
        {
            return ex.Types.Where(t => t != null)!;
        }
        catch
        {
            return Array.Empty<Type>();
        }
    }

    private static bool IsInteropAssembly(Assembly asm)
    {
        try
        {
            return asm.GetReferencedAssemblies().Any(r => r.Name == "Il2CppInterop.Runtime")
                   && asm.GetName().Name != typeof(TypeCatalog).Assembly.GetName().Name;
        }
        catch
        {
            return false;
        }
    }

    private static (string group, int rank) Classify(string name, bool native)
    {
        if (name is "Assembly-CSharp" or "Assembly-CSharp-firstpass")
            return ("game", 0);
        if (native && !name.StartsWith("UnityEngine", StringComparison.Ordinal) && !name.StartsWith("Unity.", StringComparison.Ordinal)
            && !name.StartsWith("Il2Cpp", StringComparison.Ordinal))
            return ("game", 1);
        if (native && name.StartsWith("Unity", StringComparison.Ordinal))
            return ("unity", 2);
        if (native)
            return ("il2cpp", 3);
        if (name.StartsWith("System", StringComparison.Ordinal) || name.StartsWith("Microsoft", StringComparison.Ordinal)
            || name is "mscorlib" or "netstandard")
            return ("bcl", 5);
        return ("mods", 4);
    }

    internal static string KindOf(Type type)
    {
        if (type.IsEnum)
            return "enum";
        if (type.IsInterface)
            return "interface";
        if (type.IsValueType)
            return "struct";
        if (type.IsAbstract && type.IsSealed)
            return "static";
        if (typeof(Delegate).IsAssignableFrom(type) || IsIl2CppDelegate(type))
            return "delegate";
        return "class";
    }

    private static bool IsIl2CppDelegate(Type type)
    {
        for (var t = type.BaseType; t != null; t = t.BaseType)
        {
            if (t.FullName == "Il2CppSystem.Delegate")
                return true;
        }

        return false;
    }

    private static bool IsUnityObject(Type type)
    {
        for (var t = type; t != null; t = t.BaseType)
        {
            if (t.FullName == "UnityEngine.Object")
                return true;
        }

        return false;
    }

    private static string OuterNamespace(Type type)
    {
        var outer = type;
        while (outer.IsNested && outer.DeclaringType != null)
            outer = outer.DeclaringType;
        return outer.Namespace ?? "";
    }
}
