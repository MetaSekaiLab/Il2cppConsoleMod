using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp.Scripting;
using Microsoft.CodeAnalysis.Scripting;

namespace Il2cppConsoleMod;

/// <summary>
/// 交互式 C# 会话（类似 csi / C# Interactive）。
/// 裸语句直接执行，变量跨提交持久；提交以不带分号的表达式结尾时自动返回其值。
///
/// 实现要点：必须复用同一个 Script 实例（通过 ScriptState.ContinueWithAsync 续跑），
/// 每次新建 CSharpScript 再 RunFromAsync 会抛 "starting state is incompatible with the script"。
/// 程序集引用在首次提交时快照，会话期间保持不变。
/// 编译在调用线程完成，只有执行切到 Unity 主线程，编译耗时不会卡住游戏帧。
/// </summary>
public sealed class CSharpScriptSession
{
    private static readonly string[] DefaultImports =
    {
        "System",
        "System.Linq",
        "System.Collections.Generic",
        "System.Text",
        "System.Threading.Tasks",
        "UnityEngine"
    };

    public static readonly TimeSpan ExecutionTimeout = TimeSpan.FromSeconds(60);

    private readonly object _sync = new();
    private readonly Lazy<ScriptOptions> _options = new(() =>
        ScriptOptions.Default
            .WithReferences(CSharpExecutor.BuildReferences())
            .WithImports(DefaultImports));

    private volatile ScriptState? _state;

    public ScriptOptions Options => _options.Value;

    public CSharpExecutor.CSharpResult Submit(string source) => Run(source, stringify: true, out _);

    /// <summary>
    /// Same as <see cref="Submit(string)"/> but hands back the raw return value for the
    /// inspector instead of JSON-serializing it (which would walk whole Il2Cpp object graphs).
    /// </summary>
    public CSharpExecutor.CSharpResult Submit(string source, out object? value) => Run(source, stringify: false, out value);

    private CSharpExecutor.CSharpResult Run(string source, bool stringify, out object? value)
    {
        value = null;
        lock (_sync)
        {
            var previous = _state;
            var script = previous == null
                ? CSharpScript.Create(source, _options.Value)
                : previous.Script.ContinueWith(source);

            // Compile and emit on the calling (HTTP) thread; only execution costs a game frame.
            var errors = script.Compile().Where(d => d.Severity == DiagnosticSeverity.Error).ToList();
            if (errors.Count > 0)
            {
                return new CSharpExecutor.CSharpResult
                {
                    Success = false,
                    Output = "",
                    Error = "编译失败:\n" + string.Join("\n", errors.Select(d => d.ToString()))
                };
            }

            var output = new StringWriter();
            try
            {
                var (state, resultText) = MainThreadDispatcher.Run(() =>
                {
                    var originalOut = Console.Out;
                    Console.SetOut(output);
                    try
                    {
                        var task = previous == null ? script.RunAsync() : script.RunFromAsync(previous);
                        var ran = task.GetAwaiter().GetResult();
                        // Serializing reads Il2Cpp members, so it stays on the main thread too.
                        var text = ran.ReturnValue == null || !stringify ? null : CSharpExecutor.ConvertToString(ran.ReturnValue);
                        return (ran, text);
                    }
                    finally
                    {
                        Console.SetOut(originalOut);
                    }
                }, ExecutionTimeout);

                _state = state;

                value = state.ReturnValue;
                return new CSharpExecutor.CSharpResult
                {
                    Success = true,
                    Output = output.ToString(),
                    Result = resultText,
                    HasResult = value != null
                };
            }
            catch (TimeoutException)
            {
                throw;
            }
            catch (CompilationErrorException ex)
            {
                var diagnostics = string.Join("\n", ex.Diagnostics.Select(d => d.ToString()));
                return new CSharpExecutor.CSharpResult
                {
                    Success = false,
                    Output = output.ToString(),
                    Error = "编译失败:\n" + diagnostics
                };
            }
            catch (Exception ex)
            {
                return new CSharpExecutor.CSharpResult
                {
                    Success = false,
                    Output = output.ToString(),
                    Error = ex.ToString()
                };
            }
        }
    }

    /// <summary>
    /// A not-yet-run continuation of the session, for semantic analysis (completion).
    /// Safe off the main thread: scripts and compilations are immutable.
    /// </summary>
    public Script ContinueWith(string source)
    {
        var state = _state;
        return state == null ? CSharpScript.Create(source, _options.Value) : state.Script.ContinueWith(source);
    }

    /// <summary>Session variables, latest declaration per name.</summary>
    public IReadOnlyList<ScriptVariable> Variables()
    {
        var state = _state;
        if (state == null)
            return Array.Empty<ScriptVariable>();

        var latest = new Dictionary<string, ScriptVariable>(StringComparer.Ordinal);
        foreach (var variable in state.Variables)
            latest[variable.Name] = variable;
        return latest.Values.ToList();
    }

    public void Reset()
    {
        lock (_sync)
        {
            _state = null;
        }
    }
}
