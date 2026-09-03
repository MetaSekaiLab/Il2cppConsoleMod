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
`GET /help` 可查看简要说明（同样需要 token）。

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

## 构建
```
dotnet build -c Release
```
输出到 `BepInEx/plugins`，只需部署 `Il2cppConsoleMod.dll`。
