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

## 构建
```
dotnet build -c Release
```
输出到 `BepInEx/plugins`，只需部署 `Il2cppConsoleMod.dll`。
