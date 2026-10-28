using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp.Scripting;

namespace Il2cppConsoleMod.Inspector;

/// <summary>
/// Live watch expressions. Each distinct expression is compiled once into a standalone
/// delegate (not chained onto the console session, so polling never grows session
/// state) and re-run cheaply on every refresh. They can use statics and <c>$n</c>
/// handles, but not console variables.
/// </summary>
public static class WatchEvaluator
{
    private const int MaxCached = 256;

    private static readonly object Sync = new();
    private static readonly Dictionary<string, Compiled> Cache = new(StringComparer.Ordinal);

    public sealed class Compiled
    {
        public Func<object?>? Run { get; init; }
        public string? Error { get; init; }
    }

    /// <summary>Compiles (or fetches) an expression. Compiler-only; call off the main thread.</summary>
    public static Compiled Compile(CSharpScriptSession session, string expression)
    {
        var code = HandleRewriter.Rewrite(expression);
        lock (Sync)
        {
            if (Cache.TryGetValue(code, out var cached))
                return cached;
        }

        Compiled compiled;
        try
        {
            var script = CSharpScript.Create<object>(code, session.Options);
            var errors = script.Compile().Where(d => d.Severity == DiagnosticSeverity.Error).ToList();
            if (errors.Count > 0)
            {
                compiled = new Compiled { Error = string.Join("\n", errors.Select(e => e.GetMessage())) };
            }
            else
            {
                var runner = script.CreateDelegate();
                compiled = new Compiled { Run = () => runner().GetAwaiter().GetResult() };
            }
        }
        catch (Exception ex)
        {
            compiled = new Compiled { Error = ValueDescriber.Message(ex) };
        }

        lock (Sync)
        {
            if (Cache.Count >= MaxCached)
                Cache.Clear();
            Cache[code] = compiled;
        }

        return compiled;
    }
}
