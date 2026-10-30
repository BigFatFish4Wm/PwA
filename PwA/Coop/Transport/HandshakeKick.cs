using System.Reflection;
using MegaCrit.Sts2.Core.Multiplayer;

namespace PwA.Coop.Transport;

/// <summary>
/// 游戏的 HandshakeManager 在建连后不会主动写第一包（ENet 的传输层握手会替它做），
/// broker 没有这一步，所以这里反射它的 _handshakeManager 补一次 WriteHandshakeMessage。
/// </summary>
internal static class HandshakeKick
{
    private const BindingFlags Flags = BindingFlags.Instance | BindingFlags.NonPublic;

    public static void WriteForClient(object gameService, ulong hostNetId)
    {
        TryWrite(gameService, hostNetId);
    }

    public static void WriteForHost(object gameService, ulong peerId)
    {
        TryWrite(gameService, peerId);
    }

    private static void TryWrite(object gameService, ulong peerId)
    {
        try
        {
            var field = gameService.GetType().GetField("_handshakeManager", Flags);
            var manager = field?.GetValue(gameService);
            if (manager == null)
            {
                CoopLog.Write($"握手套件缺失: {gameService.GetType().Name}");
                return;
            }

            var method = manager.GetType().GetMethod("WriteHandshakeMessage", new[] { typeof(ulong) });
            if (method == null)
            {
                CoopLog.Write("WriteHandshakeMessage(ulong) 未找到");
                return;
            }

            method.Invoke(manager, new object[] { peerId });
            CoopLog.Write($"已补发握手包 peer={peerId}（{gameService.GetType().Name}）");
        }
        catch (Exception exception)
        {
            CoopLog.Write($"补发握手包失败: {exception.GetType().Name}: {exception.Message}");
        }
    }
}
