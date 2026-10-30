using System.Reflection;
using HarmonyLib;
using MegaCrit.Sts2.Core.Platform;
using PwA.Coop.Runtime;

namespace PwA.Coop.Patches;

/// <summary>
/// 一台机器上运行多个窗口时，游戏的「本地玩家 id」默认来自 --clientId（离线时）。
/// 但传输层给每个窗口分配的 NetId 是 clientIndex+1，两者不一致时客户端会认不出
/// 自己在 RunState 里的玩家，进房间后场景是空的。这里统一成传输层的 NetId。
/// </summary>
[HarmonyPatch(typeof(PlatformUtil))]
public static class BrokerLocalPlayerIdPatch
{
    [HarmonyPrefix]
    [HarmonyPatch(nameof(PlatformUtil.GetLocalPlayerId))]
    public static bool Prefix(PlatformType platformType, ref ulong __result)
    {
        if (platformType != PlatformType.None)
        {
            return true;
        }

        var settings = CoopRuntime.Settings;
        if (settings is not { Enabled: true, Config: { } config })
        {
            return true;
        }

        __result = (ulong)(config.ClientIndex + 1);
        return false;
    }
}
