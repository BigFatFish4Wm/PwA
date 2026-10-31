using System.Reflection;
using HarmonyLib;
using MegaCrit.Sts2.Core.Logging;
using PwA.Coop.Broker;
using PwA.Coop.Runtime;
using PwA.Coop.Transport;

namespace PwA.Coop;

/// <summary>
/// 本机双窗口联机（broker 传输）。host 窗口在本进程内跑 TCP 中继；client 窗口连它。
/// 不用官方 -fastmp loopback，避免之前项目遇到的状态不同步。
/// </summary>
public static class CoopRuntime
{
    private const string LogPrefix = "[PwA.Coop]";

    private static BrokerTcpServer? _relay;
    private static int _started;
    private static Harmony? _harmony;

    public static bool Enabled { get; private set; }

    public static BrokerModeSettings? Settings { get; private set; }

    public static void Start()
    {
        if (Interlocked.Exchange(ref _started, 1) != 0)
        {
            return;
        }

        var modDirectory = Path.GetDirectoryName(typeof(CoopRuntime).Assembly.Location) ?? AppContext.BaseDirectory;
        var settings = BrokerModeSettings.LoadFromDirectory(modDirectory);
        Settings = settings;
        if (!settings.Enabled)
        {
            CoopLog.Write($"未启用（{nameof(BrokerModeSettings.RoleVariable)} 不是 host/client）");
            return;
        }

        Enabled = true;
        var config = settings.Config!;
        CoopLog.Write($"启用 role={config.Role} index={config.ClientIndex} endpoint={config.Endpoint} session={config.SessionId}");

        if (config.Role == Protocol.BrokerClientRole.Host)
        {
            StartRelay(settings, config);
        }

        BrokerTransportPump.Start();
        InstallPatches();
    }

    private static void StartRelay(BrokerModeSettings settings, Protocol.BrokerClientConfig config)
    {
        try
        {
            // 默认只听回环；要开三人房/远程玩家时设 PWA_COOP_BIND=0.0.0.0。
            if (!System.Net.IPAddress.TryParse(settings.BindAddress, out var address))
            {
                address = System.Net.IPAddress.Loopback;
            }
            var relay = new BrokerTcpServer(config.SessionId, address, config.Port, message => CoopLog.Write($"{message}"), config.Token);
            _ = Task.Run(async () =>
            {
                try
                {
                    await relay.StartAsync(CancellationToken.None);
                    CoopLog.Write($"broker 中继已在 {config.Endpoint} 监听（session={config.SessionId}）");
                }
                catch (Exception exception)
                {
                    CoopLog.Write($"ERROR broker 中继启动失败: {exception.Message}");
                }
            });
            _relay = relay;
        }
        catch (Exception exception)
        {
            CoopLog.Write($"ERROR 无法启动 broker 中继: {exception}");
        }
    }

    /// <summary>relay 上当前注册的窗口下标；HTTP 线程可安全调用。</summary>
    public static int[] PeerIndices()
    {
        var relay = _relay;
        if (relay == null)
        {
            return Array.Empty<int>();
        }

        try
        {
            return relay.ClientIndices;
        }
        catch
        {
            return Array.Empty<int>();
        }
    }

    private static void InstallPatches()
    {
        try
        {
            _harmony = new Harmony("pwa.coop");
            var patchTypes = typeof(CoopRuntime).Assembly
                .GetTypes()
                .Where(type => type.Namespace == "PwA.Coop.Patches")
                .Where(type => type.GetCustomAttributes(typeof(HarmonyPatch), inherit: false).Length > 0)
                .ToList();

            foreach (var type in patchTypes)
            {
                try
                {
                    _harmony.CreateClassProcessor(type).Patch();
                    CoopLog.Write($"已安装补丁 {type.Name}");
                }
                catch (Exception exception)
                {
                    CoopLog.Write($"ERROR 补丁 {type.Name} 安装失败: {exception.Message}");
                }
            }
        }
        catch (Exception exception)
        {
            CoopLog.Write($"ERROR 安装补丁失败: {exception}");
        }
    }

    public static void Stop()
    {
        try
        {
            _harmony?.UnpatchAll("pwa.coop");
        }
        catch
        {
        }

        try
        {
            _relay?.DisposeAsync().AsTask().Wait(TimeSpan.FromSeconds(2));
        }
        catch
        {
        }
    }
}