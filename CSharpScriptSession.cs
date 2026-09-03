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

    private readonly object _sync = new();
    private readonly Lazy<ScriptOptions> _options = new(() =>
        ScriptOptions.Default
            .WithReferences(CSharpExecutor.BuildReferences())
            .WithImports(DefaultImports));

    private ScriptState? _state;

    public CSharpExecutor.CSharpResult Submit(string source)
    {
        lock (_sync)
        {
            var output = new StringWriter();
            var originalOut = Console.Out;

            try
            {
                Console.SetOut(output);

                var state = _state == null
                    ? CSharpScript.Create(source, _options.Value).RunAsync().GetAwaiter().GetResult()
                    : _state.ContinueWithAsync(source).GetAwaiter().GetResult();

                _state = state;

                var value = state.ReturnValue;
                return new CSharpExecutor.CSharpResult
                {
                    Success = true,
                    Output = output.ToString(),
                    Result = value == null ? null : CSharpExecutor.ConvertToString(value),
                    HasResult = value != null
                };
            }
            catch (CompilationErrorException ex)
            {
                var errors = string.Join("\n", ex.Diagnostics.Select(d => d.ToString()));
                return new CSharpExecutor.CSharpResult
                {
                    Success = false,
                    Output = output.ToString(),
                    Error = "编译失败:\n" + errors
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
            finally
            {
                Console.SetOut(originalOut);
            }
        }
    }

    public void Reset()
    {
        lock (_sync)
        {
            _state = null;
        }
    }
}
