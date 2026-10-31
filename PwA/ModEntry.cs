using System.Threading;
using MegaCrit.Sts2.Core.Logging;
using MegaCrit.Sts2.Core.Modding;
using MegaCrit.Sts2.Core.Models;
using PwA.Config;
using PwA.Coop;
using PwA.Game;
using PwA.Server;

namespace PwA;

/// <summary>
/// Play with Agent 的桥接 Mod：只把一个游戏窗口的状态和原子动作暴露成本机 HTTP / MCP，
/// 不做任何决策，也不接管 UI。端口按角色分配（单人 8080 / host 8081 / client 8082）。
/// </summary>
[ModInitializer(nameof(Initialize))]
public static class ModEntry
{
    public const string Version = "1.4.1";
    private const string LogPrefix = "[PwA]";

    private static int _shutdownStarted;

    public static string GameVersion { get; } = ReadGameVersion();

    public static void Initialize()
    {
        Log.Info($"{LogPrefix} 初始化 mod {Version}（游戏 {GameVersion}）");
        try
        {
            GameThread.Initialize();
            InstanceRole.LogOnce();

            // 联机传输（broker）。未设置 PWA_COOP 时完全不动，单机行为不变。
            CoopRuntime.Start();

            var settings = PwA.Config.ModSettings.Load();
            if (!settings.Enabled)
            {
                Log.Info($"{LogPrefix} PWA_ENABLED=0，只加载不启动服务。");
                return;
            }

            BridgeServer.Start(settings);
            StatePulse.Start();
            AppDomain.CurrentDomain.ProcessExit += (_, _) => Shutdown();
            AppDomain.CurrentDomain.DomainUnload += (_, _) => Shutdown();
            Log.Info($"{LogPrefix} 就绪：http://127.0.0.1:{BridgeServer.Instance?.Port}/mcp （MCP）/ /state （REST）");
        }
        catch (Exception ex)
        {
            Log.Error($"{LogPrefix} 启动失败: {ex}");
        }
    }

    public static void Shutdown()
    {
        if (Interlocked.Exchange(ref _shutdownStarted, 1) != 0)
        {
            return;
        }

        try
        {
            StatePulse.Stop();
            BridgeServer.Instance?.Stop();
            CoopRuntime.Stop();
        }
        catch (Exception ex)
        {
            Log.Warn($"{LogPrefix} 关闭时出错: {ex.Message}");
        }
    }

    private static string ReadGameVersion()
    {
        try
        {
            return typeof(CardModel).Assembly.GetName().Version?.ToString() ?? "unknown";
        }
        catch
        {
            return "unknown";
        }
    }
}
