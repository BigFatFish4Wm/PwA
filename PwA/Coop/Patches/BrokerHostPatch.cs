using System.Reflection;
using HarmonyLib;
using MegaCrit.Sts2.Core.Entities.Multiplayer;
using MegaCrit.Sts2.Core.Logging;
using MegaCrit.Sts2.Core.Multiplayer;
using MegaCrit.Sts2.Core.Platform;
using PwA.Coop.Runtime;
using PwA.Coop.Transport;

namespace PwA.Coop.Patches;

/// <summary>
/// host 侧：游戏想开 ENet/Steam 主机时，改成开一个 broker 传输，挂在同一个 NetHostGameService 上。
/// </summary>
[HarmonyPatch(typeof(NetHostGameService))]
public static class BrokerHostPatch
{
    private const string LogPrefix = "[PwA.Coop]";

    private static readonly FieldInfo? NetHostField =
        AccessTools.Field(typeof(NetHostGameService), "_netHost");

    [HarmonyPrefix]
    [HarmonyPatch(nameof(NetHostGameService.StartENetHost))]
    public static bool StartENetHostPrefix(NetHostGameService __instance, ref NetErrorInfo? __result)
    {
        // 注意：Harmony prefix 返回 false 才是「跳过原方法」。
        // 之前这里直接返回了「是否已接管」，语义正好相反，于是接着跑了原方法，
        // 把 _netHost 又换成了 ENetHost。
        var handled = TryStartBrokerHostCore(__instance, out __result);
        return !handled;
    }

    [HarmonyPrefix]
    [HarmonyPatch(nameof(NetHostGameService.StartSteamHost))]
    public static bool StartSteamHostPrefix(NetHostGameService __instance, ref Task<NetErrorInfo?> __result)
    {
        if (!TryStartBrokerHostCore(__instance, out var error))
        {
            return true;
        }

        __result = Task.FromResult(error);
        return false;
    }

    private static bool TryStartBrokerHost(NetHostGameService instance, ref NetErrorInfo? error)
    {
        return TryStartBrokerHostCore(instance, out error);
    }

    private static bool TryStartBrokerHostCore(NetHostGameService instance, out NetErrorInfo? error)
    {
        error = null;
        var settings = CoopRuntime.Settings;
        CoopLog.Write($"StartENetHost 进入：enabled={settings?.Enabled} role={settings?.Config?.Role} 字段={(NetHostField?.Name ?? "null")}");
        if (settings is not { Enabled: true, Config: { Role: Protocol.BrokerClientRole.Host } config })
        {
            return false;
        }

        try
        {
            var host = new BrokerNetHost(instance, config, message => CoopLog.Write($"{message}"));
            host.Start();
            if (NetHostField == null)
            {
                CoopLog.Write($"找不到 NetHost 字段，传输切换失败（{instance.GetType().FullName}）");
                return false;
            }

            NetHostField.SetValue(instance, host);
            CoopLog.Write($"host 传输已切换为 broker（字段={NetHostField.Name}，当前={instance.NetHost?.GetType().Name}）");
            error = null;
            return true;
        }
        catch (Exception exception)
        {
            CoopLog.Write($"ERROR 启动 broker host 失败: {exception}");
            error = new NetErrorInfo(NetError.UnknownNetworkError, selfInitiated: true);
            return true;
        }
    }
}
