using System.Reflection;
using System.Runtime.Loader;
using System.Text.Json;

using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;

namespace Il2cppConsoleMod;

public static class CSharpExecutor
{
    public sealed class CSharpResult
    {
        public bool Success { get; set; }
        public bool HasResult { get; set; }
        public string? Output { get; set; }
        public string? Result { get; set; }
        public string? Error { get; set; }
    }

    private static readonly object Sync = new();

    public static CSharpResult Execute(string source)
    {
        lock (Sync)
        {
            var output = new StringWriter();
            var originalOut = Console.Out;

            try
            {
                Console.SetOut(output);

                // Load each payload into a fresh collectible ALC so repeated /exec
                // calls do not permanently accumulate assemblies in the default ALC.
                var alc = new CollectibleAssemblyLoadContext();
                try
                {
                    var returnValue = CompileAndInvoke(alc, source);
                    var resultText = returnValue == null ? null : ConvertToString(returnValue);
                    return new CSharpResult
                    {
                        Success = true,
                        Output = output.ToString(),
                        Result = resultText
                    };
                }
                catch (Exception ex)
                {
                    return new CSharpResult
                    {
                        Success = false,
                        Output = output.ToString(),
                        Error = ex.ToString()
                    };
                }
                finally
                {
                    // Best effort: unload only completes once the payload and its
                    // results are no longer referenced anywhere. Payloads that stash
                    // state into the game (static fields, events, threads) keep the
                    // ALC alive until the game restarts.
                    alc.Unload();
                }
            }
            finally
            {
                Console.SetOut(originalOut);
            }
        }
    }

    private static object? CompileAndInvoke(AssemblyLoadContext alc, string source)
    {
        var references = BuildReferences();

        var tree = CSharpSyntaxTree.ParseText(source);
        var compilation = CSharpCompilation.Create(
            "DynamicPayload_" + Guid.NewGuid().ToString("N"),
            new[] { tree },
            references,
            new CSharpCompilationOptions(
                OutputKind.DynamicallyLinkedLibrary,
                allowUnsafe: true,
                optimizationLevel: OptimizationLevel.Debug));

        using var peStream = new MemoryStream();
        var emitResult = compilation.Emit(peStream);

        if (!emitResult.Success)
        {
            var errors = string.Join(
                "\n",
                emitResult.Diagnostics
                    .Where(d => d.Severity == DiagnosticSeverity.Error)
                    .Select(d => d.ToString()));

            throw new InvalidOperationException("编译失败:\n" + errors);
        }

        peStream.Position = 0;
        var assembly = alc.LoadFromStream(peStream);

        var type = assembly.GetType("Payload")
                   ?? assembly.GetTypes().FirstOrDefault(t =>
                       t.GetMethod("Execute", BindingFlags.Public | BindingFlags.Static) != null)
                   ?? throw new InvalidOperationException(
                       "未找到类型 Payload，请定义 public static class Payload");

        var method = type.GetMethod("Execute", BindingFlags.Public | BindingFlags.Static)
            ?? throw new InvalidOperationException(
                "未找到方法 Execute，请定义 public static object Execute()");

        return method.Invoke(null, null);
    }

    // References are cached and only rebuilt when the set of loaded assembly
    // locations changes, so repeated /exec calls do not re-read metadata from
    // every assembly on disk each time. Assemblies loaded later are picked up
    // automatically on the next rebuild.
    private static readonly object ReferencesSync = new();
    private static List<MetadataReference>? _cachedReferences;
    private static HashSet<string>? _cachedLocations;

    internal static List<MetadataReference> BuildReferences()
    {
        var locations = AppDomain.CurrentDomain.GetAssemblies()
            .Where(a => !a.IsDynamic && !string.IsNullOrEmpty(a.Location))
            .Select(a => a.Location)
            .ToList();

        lock (ReferencesSync)
        {
            if (_cachedReferences == null || _cachedLocations == null ||
                !_cachedLocations.SetEquals(locations))
            {
                var references = new List<MetadataReference>(locations.Count);
                foreach (var location in locations)
                {
                    try
                    {
                        references.Add(MetadataReference.CreateFromFile(location));
                    }
                    catch
                    {
                        // ignore assemblies whose metadata cannot be read
                    }
                }

                _cachedReferences = references;
                _cachedLocations = new HashSet<string>(locations, StringComparer.Ordinal);
            }

            // Return a copy so callers cannot mutate the shared cache.
            return new List<MetadataReference>(_cachedReferences);
        }
    }

    internal static string? ConvertToString(object? value)
    {
        if (value == null)
            return null;

        if (value is string s)
            return s;

        try
        {
            return JsonSerializer.Serialize(value);
        }
        catch
        {
            return value.ToString();
        }
    }

    private sealed class CollectibleAssemblyLoadContext : AssemblyLoadContext
    {
        public CollectibleAssemblyLoadContext() : base(isCollectible: true)
        {
        }
    }
}
