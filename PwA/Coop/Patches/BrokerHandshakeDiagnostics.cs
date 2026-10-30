using System.Reflection;
using HarmonyLib;
using MegaCrit.Sts2.Core.Multiplayer.Connection;

namespace PwA.Coop.Patches;

/// <summary>临时诊断：看握手管理器到底走到哪一步（连上后没有发出任何包）。</summary>
[HarmonyPatch]
public static class BrokerHandshakeDiagnostics
{
    [HarmonyPatch(typeof(HandshakeManager), nameof(HandshakeManager.BeginHandshakeFor))]
    [HarmonyPrefix]
    public static void BeginPrefix(ulong peerId)
    {
        CoopLog.Write($"HandshakeManager.BeginHandshakeFor({peerId})");
    }

    [HarmonyPatch(typeof(HandshakeManager), nameof(HandshakeManager.BeginHandshakeFor))]
    [HarmonyFinalizer]
    public static void BeginFinalizer(Exception? __exception)
    {
        CoopLog.Write($"BeginHandshakeFor 结束 exception={__exception?.GetType().Name}: {__exception?.Message}");
    }

    [HarmonyPatch(typeof(HandshakeManager), nameof(HandshakeManager.WriteHandshakeMessage), new[] { typeof(ulong) })]
    [HarmonyPrefix]
    public static void WritePrefix(ulong peerId)
    {
        CoopLog.Write($"WriteHandshakeMessage({peerId})");
    }

    [HarmonyPatch(typeof(HandshakeManager), nameof(HandshakeManager.WriteHandshakeMessage), new[] { typeof(ulong) })]
    [HarmonyFinalizer]
    public static void WriteFinalizer(Exception? __exception)
    {
        CoopLog.Write($"WriteHandshakeMessage 结束 exception={__exception?.GetType().Name}: {__exception?.Message}");
    }

    [HarmonyPatch(typeof(HandshakeManager), nameof(HandshakeManager.WriteHandshakeMessage), new[] { typeof(ulong), typeof(MegaCrit.Sts2.Core.Multiplayer.PeerVersionInfo), typeof(MegaCrit.Sts2.Core.Multiplayer.Serialization.PacketWriter) })]
    [HarmonyPrefix]
    public static void Write3Prefix(ulong peerId)
    {
        CoopLog.Write($"WriteHandshakeMessage3({peerId})");
    }

    [HarmonyPatch(typeof(HandshakeManager), nameof(HandshakeManager.WriteHandshakeMessage), new[] { typeof(ulong), typeof(MegaCrit.Sts2.Core.Multiplayer.PeerVersionInfo), typeof(MegaCrit.Sts2.Core.Multiplayer.Serialization.PacketWriter) })]
    [HarmonyFinalizer]
    public static void Write3Finalizer(Exception? __exception)
    {
        CoopLog.Write($"WriteHandshakeMessage3 结束 exception={__exception?.GetType().Name}: {__exception?.Message}");
    }

    [HarmonyPatch(typeof(MegaCrit.Sts2.Core.Multiplayer.NetClientGameService), nameof(MegaCrit.Sts2.Core.Multiplayer.NetClientGameService.SendHandshakeMessage))]
    [HarmonyPrefix]
    public static void ClientSendPrefix(ulong peerId)
    {
        CoopLog.Write($"NetClientGameService.SendHandshakeMessage({peerId})");
    }

    [HarmonyPatch(typeof(MegaCrit.Sts2.Core.Multiplayer.NetClientGameService), nameof(MegaCrit.Sts2.Core.Multiplayer.NetClientGameService.SendHandshakeMessage))]
    [HarmonyFinalizer]
    public static void ClientSendFinalizer(Exception? __exception)
    {
        CoopLog.Write($"SendHandshakeMessage 结束 exception={__exception?.GetType().Name}: {__exception?.Message}");
    }

    [HarmonyPatch(typeof(HandshakeManager), nameof(HandshakeManager.HandshakeMessageReceived))]
    [HarmonyPrefix]
    public static void ReceivedPrefix(ulong senderId)
    {
        CoopLog.Write($"HandshakeMessageReceived({senderId})");
    }

    [HarmonyPatch(typeof(HandshakeManager), nameof(HandshakeManager.HandshakeMessageReceived))]
    [HarmonyFinalizer]
    public static void ReceivedFinalizer(Exception? __exception)
    {
        CoopLog.Write($"HandshakeMessageReceived 结束 exception={__exception?.GetType().Name}: {__exception?.Message}");
    }

    [HarmonyPatch(typeof(MegaCrit.Sts2.Core.Multiplayer.NetHostGameService), nameof(MegaCrit.Sts2.Core.Multiplayer.NetHostGameService.SendHandshakeMessage))]
    [HarmonyPrefix]
    public static void HostSendPrefix(MegaCrit.Sts2.Core.Multiplayer.NetHostGameService __instance, ulong peerId)
    {
        CoopLog.Write($"NetHostGameService.SendHandshakeMessage({peerId}) service={__instance.GetType().FullName} netHost={__instance.NetHost?.GetType().Name}");
    }

    [HarmonyPatch(typeof(MegaCrit.Sts2.Core.Multiplayer.Transport.ENet.ENetHost), nameof(MegaCrit.Sts2.Core.Multiplayer.Transport.ENet.ENetHost.StartHost))]
    [HarmonyPrefix]
    public static void ENetStartPrefix(ushort port, int maxClients)
    {
        CoopLog.Write($"!!! ENetHost.StartHost(port={port}, max={maxClients}) 被调用\n{Environment.StackTrace}");
    }

}
