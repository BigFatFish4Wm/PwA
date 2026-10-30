using MegaCrit.Sts2.Core.Logging;
using MegaCrit.Sts2.Core.Multiplayer.Game;
using MegaCrit.Sts2.Core.Runs;

namespace PwA.Game;

/// <summary>本窗口是 host 还是 client。端口默认值和 /health 都靠它。</summary>
internal static class InstanceRole
{
    private const string LogPrefix = "[PwA.Role]";

    public enum Role
    {
        Unknown,
        Singleplayer,
        Host,
        Client,
    }

    private static Role _last = Role.Unknown;

    public static Role Current
    {
        get
        {
            var live = Detect();
            if (live != Role.Unknown)
            {
                _last = live;
            }

            return _last;
        }
    }

    /// <summary>多开时给 World 的额外提示：本窗口是不是后起的那个（进程启动时间更晚）。</summary>
    public static DateTime ProcessStartedAt { get; } = StartTimeOfCurrentProcess();

    public static void LogOnce()
    {
        Log.Info($"{LogPrefix} 当前角色 {Current}（pid={Environment.ProcessId}）");
    }

    private static Role Detect()
    {
        // 联机（broker）模式：角色由启动参数决定，不再依赖 NetService 的类型
        // （broker 的传输类型和游戏原生不一致，读出来经常是 Unknown）。
        if (PwA.Coop.CoopRuntime.Settings is { Enabled: true, Config: { } coopConfig })
        {
            return coopConfig.Role == PwA.Coop.Protocol.BrokerClientRole.Host ? Role.Host : Role.Client;
        }

        try
        {
            var net = RunManager.Instance?.NetService;
            if (net == null)
            {
                return Role.Unknown;
            }

            return net.Type switch
            {
                NetGameType.Singleplayer => Role.Singleplayer,
                NetGameType.Host => Role.Host,
                NetGameType.Client => Role.Client,
                _ => Role.Unknown,
            };
        }
        catch
        {
            return Role.Unknown;
        }
    }

    private static DateTime StartTimeOfCurrentProcess()
    {
        try
        {
            return System.Diagnostics.Process.GetCurrentProcess().StartTime;
        }
        catch
        {
            return DateTime.Now;
        }
    }
}
