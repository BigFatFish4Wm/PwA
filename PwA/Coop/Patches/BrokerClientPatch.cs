using System.Reflection;
using HarmonyLib;
using MegaCrit.Sts2.Core.Entities.Multiplayer;
using MegaCrit.Sts2.Core.Logging;
using MegaCrit.Sts2.Core.Multiplayer;
using MegaCrit.Sts2.Core.Multiplayer.Connection;
using MegaCrit.Sts2.Core.Multiplayer.Game;
using MegaCrit.Sts2.Core.Platform;
using PwA.Coop.Runtime;
using PwA.Coop.Transport;

namespace PwA.Coop.Patches;

/// <summary>
/// client 侧：任何走 ENet 的加入流程（-fastmp join / 好友列表）都改成连 broker。
/// </summary>
[HarmonyPatch(typeof(ENetClientConnectionInitializer))]
public static class BrokerClientPatch
{
    private const string LogPrefix = "[PwA.Coop]";

    [HarmonyPrefix]
    [HarmonyPatch(nameof(ENetClientConnectionInitializer.Connect))]
    public static bool ConnectPrefix(
        INetClientGameService netService,
        CancellationToken cancelToken,
        ref Task<NetErrorInfo?> __result)
    {
        var settings = CoopRuntime.Settings;
        if (settings is not { Enabled: true, Config: { Role: Protocol.BrokerClientRole.Client } config })
        {
            return true;
        }

        if (netService is not NetClientGameService service)
        {
            return true;
        }

        try
        {
            var client = new BrokerNetClient(service, config, message => CoopLog.Write($"{message}"));
            service.Initialize(client, PlatformType.None);
            __result = client.ConnectAsync(cancelToken);
            return false;
        }
        catch (Exception exception)
        {
            CoopLog.Write($"ERROR 启动 broker client 失败: {exception}");
            __result = Task.FromResult<NetErrorInfo?>(new NetErrorInfo(NetError.UnknownNetworkError, selfInitiated: true));
            return false;
        }
    }
}
