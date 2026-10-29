# Il2cppConsoleMod

BepInEx 6 IL2CPP 运行时 C# 调试 mod，通过 HTTP 执行 C# 代码，便于 人工或 Agent 进行热调试。

## 配置
`BepInEx/config/Il2cppConsoleMod.cfg`：
```
[Server]
ListenAddress = 127.0.0.1
Port = 18765

[Security]
Enabled = false
AcknowledgeRisk = false
```
- `Enabled`：总开关。
- `AcknowledgeRisk`：确认安全风险。
- 两个都必须为 `true` 才能启动。
- 默认监听 127.0.0.1，改 0.0.0.0 可局域网访问。

## 使用
启动后日志打印随机 token，请求需带 `X-Debug-Token`：
```
curl -X POST http://127.0.0.1:18765/exec -H "Content-Type: text/plain" -H "X-Debug-Token: <token>" --data-binary @payload.cs
```
payload.cs：
```
public static class Payload { public static object Execute() => 1 + 1; }
```
`GET /help` 返回接口简述与**内嵌的完整 README**（markdown，编译时随 DLL 打包；同样需要 token）。

## Web 控制台

浏览器打开 `http://127.0.0.1:18765/`，输入日志里的 token（或打开 `/#token=<token>`，token 会立即从地址栏移除）。页面随 DLL 内嵌，所有 `/api/*` 请求同样需要 token。

- **场景**：GameObject 层级（含 DontDestroyOnLoad）、跨场景搜索、切换激活。
- **检查器**：逐层展开字段，值解析到真实运行时类型；组件卡片、集合分页、双击编辑、调用方法、贴图预览、实时刷新。橙点为 Il2Cpp 字段。
- **类型**：按程序集浏览或搜索（支持首字母缩写），查看静态成员与实例。静态属性和有副作用的 getter 只在点击时读取。
- **控制台**：与 `/script` 共用会话，Roslyn 补全；`$n` 引用检查器里的对象（带真实类型），`$_` 是上次结果。“程序”模式等价于 `/exec`。
- **会话**：收藏、监视表达式、控制台变量。**日志**：实时 BepInEx 日志。Ctrl+K 命令面板。

读取游戏对象的请求在 Unity 主线程执行，游戏加载或暂停时会等待；C# 代码在 HTTP 线程编译，只有执行切到主线程。

## Android

- 电脑上执行 `adb forward tcp:18765 tcp:18765` 后照常打开 `http://127.0.0.1:18765/`；或把 `ListenAddress` 设为 `0.0.0.0` 从局域网访问。
- 如果注入的 MonoBehaviour 收不到 `Update`（请求一直“主线程无响应”），在配置里打开 `[Compatibility] UseNativeUpdateHook = true`，改用 Dobby 挂钩 `EventSystem.Update`（需要场景里有 EventSystem）。

## 读取 BepInEx 日志（/logcat）

返回完整 `LogOutput.log` 原文（text/plain），同样需要 token：

```
curl -s http://127.0.0.1:18765/logcat -H "X-Debug-Token: <token>"
```

## 交互式会话（/script）

像 C# Interactive 控制台一样逐句执行，**变量跨请求持久**，无需 `class Payload` 包装：

```
curl -X POST http://127.0.0.1:18765/script -H "X-Debug-Token: <token>" --data-binary 'var x = 1;'
curl -X POST http://127.0.0.1:18765/script -H "X-Debug-Token: <token>" --data-binary 'x + 1'          # Result: 2
curl -X POST http://127.0.0.1:18765/script -H "X-Debug-Token: <token>" --data-binary 'var y = x + 1;'
curl -X POST http://127.0.0.1:18765/script/reset -H "X-Debug-Token: <token>"                          # 清空会话状态
```

- 裸语句即可；预置 using：`System` / `System.Linq` / `System.Collections.Generic` / `System.Text` / `System.Threading.Tasks` / `UnityEngine`。
- 提交以**不带分号的表达式**结尾时自动返回其值（响应中 `HasResult` 区分"返回了 null"与"无返回值"）。
- 脚本内可声明类型，但不能使用 `namespace`（需要完整程序时请用 `/exec`）。
- 会话状态随提交次数线性增长，长时间调试建议定期 `/script/reset`。
- 程序集引用在首次提交时快照；游戏运行中后加载的程序集不会出现在脚本里（这种情况用 `/exec`）。
- 脚本内避免 `async/await`（Unity 主线程没有同步上下文）。

## 交互式客户端

`scripts/console.py` 提供类 shell 的交互客户端（纯标准库，零依赖）：

```
python3 scripts/console.py <ip> <token> [--port 18765]
```

进入后逐句输入 C# 代码回车执行；`!reset` 清空会话，`!help` 查看帮助，`exit` 或 `Ctrl-D` 退出。

## 动态 Harmony Patch（挂载 / 验证 / 撤销）

在 `/script` 会话里可以对游戏方法做热补丁。**必须用 `/script` 而不要用 `/exec`**：`/exec` 的载荷编译进可回收 ALC，请求一结束就被卸载，补丁随之失效；`/script` 的程序集不可回收，补丁常驻。下面几点为强烈建议但非强制。

- **不要使用反射。** interop 程序集里游戏成员全部为 public，直接引用即可；il2cpp dumper 产物 DummyDll/stub 里的 `private/protected` 只是转储产物，在 BepInEx/Il2cppInterop 中被全部公有化。目标方法一律用属性 + `nameof` 绑定。
- 日志请走 BepInEx 日志源（写入 `LogOutput.log`）；`UnityEngine.Debug.Log` 在部分宿主上不可见。`Logger` 与 `UnityEngine.Logger` 撞名时用全限定 `BepInEx.Logging.Logger.CreateLogSource`。
- 以下代码中的 `Example.*` / `TargetMethod` 均为占位符，按实际目标替换。

**1. 挂载**（属性式声明，零反射）：

```csharp
using System;
using BepInEx.Logging;
using HarmonyLib;
using Example.Gameplay;

[HarmonyPatch(typeof(SomeLogic), nameof(SomeLogic.TargetMethod))]
public static class DemoHook
{
    public static int HitCount;
    private static readonly ManualLogSource Log = BepInEx.Logging.Logger.CreateLogSource("DemoHook");

    [HarmonyPrefix]
    public static bool Prefix(SomeLogic __instance)   // __instance = 本次调用实例
    {
        var n = System.Threading.Interlocked.Increment(ref HitCount);
        Log.LogWarning($"[DemoHook] hit #{n} on {__instance?.GetType().Name}");
        return false;   // false = 跳过原方法（原方法为 void 时同样生效，可用于“屏蔽”副作用）
        // return true; // true = 放行，继续执行原方法
    }
}

var hook = new Harmony("Demo.Hook");
hook.PatchAll(typeof(DemoHook).Assembly);
```

**2. 验证**（不用每次进行，仅在行为异常时可进行调试；补丁已注册 / 强制 GC 后仍存活）：

```csharp
System.GC.Collect();
System.GC.WaitForPendingFinalizers();

var n = 0;
foreach (var m in hook.GetPatchedMethods()) n++;
"patched methods: " + n          // 期望 >= 1
```

**3. 撤销**：

```csharp
hook.UnpatchSelf();                       // 撤销该实例的全部补丁
// 或 HarmonyLib.Harmony.UnpatchID("Demo.Hook");
```

**4. 语义要点**：

- Prefix 返回 `bool`：`false` = 跳过原方法体（拦截/屏蔽），`true` = 照常执行。对返回 `void` 的原方法同样有效。
- `Harmony.GetPatchInfo(...)` 在本版 HarmonyX 中是**静态**方法；`UnpatchSelf()` 已取代过时的实例版 `UnpatchAll()`。
- 计数/日志只在进程内存与 `LogOutput.log` 中，重启游戏即全部消失，不会污染存档或产生残留。

## 构建
```
dotnet build -c Release
```
输出到 `BepInEx/plugins`，只需部署 `Il2cppConsoleMod.dll`（Web 控制台的页面 `Web/wwwroot/*` 编译时内嵌进 DLL）。

代码结构：`Inspector/` 是对象检查（句柄表、反射、Il2Cpp 运行时类型解析、Unity 场景访问、补全），`Web/` 是 HTTP API、日志缓冲与前端页面（原生 JS/CSS，无构建步骤）。
