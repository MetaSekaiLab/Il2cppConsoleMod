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

浏览器打开 `http://127.0.0.1:18765/`，输入日志里的 token 即可（也可以直接打开 `http://127.0.0.1:18765/#token=<token>` 免输入，token 会立即从地址栏移除）。页面随 DLL 内嵌、不依赖外网；静态页面本身不含任何数据，所有 `/api/*` 请求同样需要 token。

- **场景**：按场景列出 GameObject 层级（含 DontDestroyOnLoad），按名称跨场景搜索，右键收藏 / 在控制台引用 / 切换激活。
- **检查器**：像浏览器开发者工具一样逐层展开字段，支持后退 / 前进与面包屑。
  - GameObject 以 Unity Inspector 式的组件卡片展示，可勾选 `enabled`、开关 `SetActive`，显示路径、Layer、Tag。
  - 成员按声明类型分组；橙点为 Il2Cpp 字段（Il2CppInterop 以属性形式暴露），空心圆为属性。值会解析到真实的运行时类型（`Component` 会显示为 `SpriteRenderer` 等），装箱的 Il2Cpp 基础类型 / 字符串 / 结构体会自动拆箱。
  - 集合（托管集合、`Il2CppArrayBase`、Il2Cpp `List` / `Dictionary` / `HashSet`、`Transform` 子节点等）分页展示元素。
  - 双击值可编辑基础类型、字符串、枚举（含 Flags）、向量与颜色；布尔值直接勾选。
  - “方法”页可填参数直接调用（`$12` 传对象、留空使用默认值），返回值同样可以展开。
  - Sprite / Texture 有“预览”页（经 RenderTexture 读回，不可读纹理也能看），可下载 PNG。
  - 右上角 ⚡ 开启实时刷新（每秒），变化的值会闪烁提示。
- **类型**：按程序集 / 命名空间浏览全部类型，或用名称、缩写（`SR` → `SpriteRenderer`）搜索；打开后查看静态成员，`UnityEngine.Object` 子类还有“实例”页（`Resources.FindObjectsOfTypeAll`）。
  - 静态**属性**默认不自动读取（单例的 `Instance` 之类 getter 可能会顺带创建对象），点击“读取”或“读取全部属性”；Il2Cpp 静态字段照常显示。`Renderer.material`、`UnityEngine.Random.value` 等有副作用的 getter 始终只在点击时读取。
- **控制台**：与 `/script` 共用同一个会话。Enter 执行（代码不完整时换行），Shift+Enter 换行，↑↓ 翻历史；输入 `.` 或 Ctrl+Space 触发基于 Roslyn 语义模型的补全（认识会话变量）。结果以可展开的对象树显示。
  - `$12` 引用检查器里的对象句柄，会被替换成**带真实类型的引用**，因此可以直接写 `$12.SetActive(false)`、`$12.transform.position`；类型句柄代表类型本身（`$7.Instance`）；`$_` 是上一次执行或方法调用的结果。插值字符串的 `{}` 里不能直接写 `$12`，先赋给变量。
  - “程序”模式等价于 `/exec`（完整 `Payload` 程序，每次独立编译）。
- **会话**：收藏对象、监视表达式（每秒求值；独立编译，可用静态成员和 `$句柄`，不能用控制台变量）、控制台变量列表。
- **日志**：实时显示 BepInEx 日志（含 Unity 日志），按级别 / 来源 / 文本过滤，可打开完整 `LogOutput.log`。
- Ctrl+K 打开命令面板：搜索类型、GameObject 和命令。

注意：

- 读取游戏对象的请求都在 Unity 主线程执行。游戏在加载、或在后台被暂停（游戏设置里没开“后台运行”，而浏览器在同一台电脑上抢走了焦点）时，请求会等待，页面右上角显示“主线程无响应”。类型浏览、补全、方法列表和日志不依赖主线程。
- C# 代码（控制台、`/script`、`/exec`）在 HTTP 线程编译，只有执行切到主线程，编译耗时不会卡住游戏帧。
- 对象句柄会让对应对象保持存活（上限 20000 个，按最近使用淘汰）；命令面板里可“清空对象句柄”。游戏重启后旧句柄全部失效，页面会自动提示。

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
