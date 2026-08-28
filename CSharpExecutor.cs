using System.Reflection;
using System.Text.Json;

using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;

namespace Il2cppConsoleMod;

public static class CSharpExecutor
{
    public sealed class CSharpResult
    {
        public bool Success { get; set; }
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
                var returnValue = CompileAndInvoke(source);

                return new CSharpResult
                {
                    Success = true,
                    Output = output.ToString(),
                    Result = returnValue == null ? null : ConvertToString(returnValue)
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
                Console.SetOut(originalOut);
            }
        }
    }

    private static object? CompileAndInvoke(string source)
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

        var assembly = Assembly.Load(peStream.ToArray());

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

    private static List<MetadataReference> BuildReferences()
    {
        var references = new List<MetadataReference>();

        foreach (var assembly in AppDomain.CurrentDomain.GetAssemblies())
        {
            if (assembly.IsDynamic || string.IsNullOrEmpty(assembly.Location))
                continue;

            try
            {
                references.Add(MetadataReference.CreateFromFile(assembly.Location));
            }
            catch
            {
                // ignore
            }
        }

        return references;
    }

    private static string? ConvertToString(object? value)
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
}
