# Il2cppConsoleMod

BepInEx 6 IL2CPP 运行时调试 mod：通过 HTTP 执行 C#，并内嵌网页控制台浏览场景、对象和类型，便于人工或 Agent 热调试。支持 Windows 与 Android。

## 配置

`BepInEx/config/Il2cppConsoleMod.cfg`：

```
[Server]
ListenAddress = 127.0.0.1
Port = 18765

[Security]
Enabled = false
AcknowledgeRisk = false

[Compatibility]
UseNativeUpdateHook = false
```

- `Enabled` 与 `AcknowledgeRisk` 都为 `true` 才会启动（可执行任意代码，注意安全）。
- `ListenAddress = 0.0.0.0` 允许局域网访问。
- `UseNativeUpdateHook` 见 [Android](#android)。

## 使用

启动后日志打印 token 和网页控制台链接（`http://127.0.0.1:18765/#token=…`，直接打开即可）。HTTP 接口需带 `X-Debug-Token` 头：

```
curl -X POST http://127.0.0.1:18765/exec -H "X-Debug-Token: <token>" --data-binary 'public static class Payload { public static object Execute() => 1 + 1; }'
```

| 接口 | 说明 |
| --- | --- |
| `POST /exec` | 完整程序（`public static class Payload` 的 `Execute()`），每次独立编译 |
| `POST /script` | 会话式执行，变量跨请求保留；`POST /script/reset` 清空会话 |
| `GET /logcat` | 完整 `LogOutput.log` |
| `GET /health` | 存活检查 |
| `GET /help` | 接口简述与本 README |

`/script` 说明：

- 裸语句即可，预置 using：`System`、`System.Linq`、`System.Collections.Generic`、`System.Text`、`System.Threading.Tasks`、`UnityEngine`。
- 以不带分号的表达式结尾时返回其值（`HasResult` 区分返回 null 与无返回值）。
- 可以声明类型，但不能用 `namespace`；需要完整程序用 `/exec`。
- 程序集引用在首次提交时快照，之后才加载的程序集请用 `/exec`；会话随提交增长，可定期 reset。
- 避免 `async/await`（Unity 主线程没有同步上下文）。

命令行客户端：`python3 scripts/console.py <host> <token> [--port 18765]`（`!reset`、`!help`、`exit`）。

## 网页控制台

页面随 DLL 内嵌，`/api/*` 同样需要 token。

- **场景**：GameObject 层级（含 DontDestroyOnLoad）、跨场景搜索、切换激活。
- **检查器**：逐层展开字段，值解析到真实运行时类型；组件卡片、集合分页、双击编辑、调用方法、贴图预览、实时刷新。橙点为 Il2Cpp 字段。
- **类型**：按程序集浏览或搜索（支持首字母缩写），查看静态成员与实例。静态属性和有副作用的 getter 只在点击时读取。
- **控制台**：与 `/script` 共用会话，Roslyn 补全；`$n` 引用检查器里的对象（带真实类型），`$_` 是上次结果；“程序”模式等价于 `/exec`。
- **会话**：收藏、监视表达式、控制台变量。**日志**：实时 BepInEx 日志。Ctrl+K 命令面板。

读取游戏对象的请求在 Unity 主线程执行，游戏加载或暂停时会等待；C# 在 HTTP 线程编译，只有执行切到主线程。

## Android

- 电脑上执行 `adb forward tcp:18765 tcp:18765`，再打开日志里的链接；或设 `ListenAddress = 0.0.0.0` 从局域网访问。
- 如果请求一直“主线程无响应”（注入的 MonoBehaviour 收不到 `Update`），设 `UseNativeUpdateHook = true`，改用 Dobby 挂钩 `EventSystem.Update`（场景里需要有 EventSystem）。

## Harmony 热补丁

在 `/script` 里挂补丁：`/exec` 的程序集执行完即卸载，补丁会随之失效。interop 程序集里的成员都是 public（转储 DummyDll 里的 private 不算数），用 `typeof` + `nameof` 绑定，不要用反射；日志走 BepInEx 日志源。

```csharp
using HarmonyLib;

[HarmonyPatch(typeof(TargetType), nameof(TargetType.TargetMethod))]
public static class Hook
{
    public static int Hits;

    [HarmonyPrefix]
    public static bool Prefix(TargetType __instance)
    {
        Hits++;
        return true; // false 跳过原方法（void 方法同样有效）
    }
}

var hook = new Harmony("Console.Hook");
hook.PatchAll(typeof(Hook).Assembly);
```

- 确认已挂上：`hook.GetPatchedMethods().Count()`；撤销：`hook.UnpatchSelf()`。
- HarmonyX 的 `Harmony.GetPatchInfo(...)` 是静态方法；`Logger` 与 `UnityEngine.Logger` 撞名时写全 `BepInEx.Logging.Logger.CreateLogSource(...)`。
- 补丁只存在于进程内，重启游戏即消失。

## 构建

```
dotnet build -c Release
```

依赖路径写在 `Il2cppConsoleMod.local.props`（参考 `.example`）。只需部署 `Il2cppConsoleMod.dll`，网页内嵌在其中。

代码结构：`Inspector/` 是对象检查（句柄表、反射、Il2Cpp 运行时类型解析、Unity 场景访问、补全），`Web/` 是 HTTP API、日志缓冲与前端页面（原生 JS/CSS，无构建步骤）。
