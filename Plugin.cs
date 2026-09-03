using BepInEx;
using BepInEx.Configuration;
using BepInEx.Unity.IL2CPP;
using Il2CppInterop.Runtime.Injection;

namespace Il2cppConsoleMod;

[BepInPlugin(MyPluginInfo.PLUGIN_GUID, MyPluginInfo.PLUGIN_NAME, MyPluginInfo.PLUGIN_VERSION)]
public class Plugin : BasePlugin
{
    private HttpDebugServer? _server;
    private CSharpScriptSession? _session;

    public override void Load()
    {
        var listenAddress = Config.Bind(
            "Server", "ListenAddress", "127.0.0.1",
            "Address to listen on. Use 0.0.0.0 to allow LAN access.\n监听地址，0.0.0.0 表示允许局域网访问。");

        var port = Config.Bind(
            "Server", "Port", 18765,
            new ConfigDescription(
                "HTTP port for the debug server.\n调试服务器 HTTP 端口。",
                new AcceptableValueRange<int>(1, 65535)));

        var enabled = Config.Bind(
            "Security", "Enabled", false,
            "Enable the HTTP debug server. This mod can execute arbitrary C# code and may lead to command execution or other security issues. Use with caution.\n" +
            "启用 HTTP 调试服务。该 mod 可执行任意 C# 代码，可能导致命令执行等安全问题，请谨慎使用。");

        var acknowledgeRisk = Config.Bind(
            "Security", "AcknowledgeRisk", false,
            "Acknowledge the security risks of arbitrary code execution. Both this and Enabled must be true to start.\n" +
            "确认任意代码执行的安全风险。此开关和 Enabled 都必须为 true 才能启动。");

        if (!enabled.Value || !acknowledgeRisk.Value)
        {
            Log.LogWarning("Il2cppConsoleMod is disabled. Set Security.Enabled=true and Security.AcknowledgeRisk=true in the config to enable.");
            return;
        }

        var token = GenerateToken();
        Log.LogWarning($"Il2cppConsoleMod token: {token}");

        try
        {
            ClassInjector.RegisterTypeInIl2Cpp<RuntimeCSharpHost>();
            RuntimeCSharpHost.Create();

            _session = new CSharpScriptSession();
            _server = new HttpDebugServer(listenAddress.Value, port.Value, token, Log, _session);
            _server.Start();
        }
        catch (Exception ex)
        {
            Log.LogError($"Il2cppConsoleMod failed to start: {ex}");
        }
    }

    public override bool Unload()
    {
        _server?.Stop();
        return true;
    }

    private static string GenerateToken() => Guid.NewGuid().ToString("N");
}
