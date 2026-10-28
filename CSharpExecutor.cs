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

    public static CSharpResult Execute(string source) => Execute(source, stringify: true, out _);

    /// <summary>For the web inspector: hands back the raw return value instead of JSON text.</summary>
    public static CSharpResult Execute(string source, out object? value) => Execute(source, stringify: false, out value);

    private static CSharpResult Execute(string source, bool stringify, out object? value)
    {
        value = null;
        lock (Sync)
        {
            var output = new StringWriter();

            // Load each payload into a fresh collectible ALC so repeated /exec
            // calls do not permanently accumulate assemblies in the default ALC.
            var alc = new CollectibleAssemblyLoadContext();
            try
            {
                // Compile and load on the calling thread; only Execute() runs on the main thread.
                var method = Compile(alc, source);
                var (returnValue, resultText) = MainThreadDispatcher.Run(() =>
                {
                    var originalOut = Console.Out;
                    Console.SetOut(output);
                    try
                    {
                        var result = method.Invoke(null, null);
                        return (result, result == null || !stringify ? null : ConvertToString(result));
                    }
                    finally
                    {
                        Console.SetOut(originalOut);
                    }
                }, CSharpScriptSession.ExecutionTimeout);

                value = returnValue;
                return new CSharpResult
                {
                    Success = true,
                    Output = output.ToString(),
                    Result = resultText
                };
            }
            catch (TimeoutException)
            {
                throw;
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
    }

    private static MethodInfo Compile(AssemblyLoadContext alc, string source)
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

        return type.GetMethod("Execute", BindingFlags.Public | BindingFlags.Static)
            ?? throw new InvalidOperationException(
                "未找到方法 Execute，请定义 public static object Execute()");
    }

    // References are cached and only rebuilt when the set of loaded assembly
    // locations changes, so repeated /exec calls do not re-read metadata from
    // every assembly on disk each time. Assemblies loaded later are picked up
    // automatically on the next rebuild.
    private static readonly object ReferencesSync = new();
    private static List<MetadataReference>? _cachedReferences;
    private static HashSet<string>? _cachedLocations;

    // Framework assemblies scripts commonly need; touching them makes sure they are
    // loaded (and therefore referenced) even if the game never used them itself.
    private static readonly Type[] BaselineTypes =
    {
        typeof(Console), typeof(Enumerable), typeof(HashSet<>), typeof(System.Text.RegularExpressions.Regex),
        typeof(System.Diagnostics.Stopwatch), typeof(System.Collections.Concurrent.ConcurrentDictionary<,>),
    };

    internal static List<MetadataReference> BuildReferences()
    {
        GC.KeepAlive(BaselineTypes);
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
