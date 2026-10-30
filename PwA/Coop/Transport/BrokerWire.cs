using System.Buffers.Binary;
using MegaCrit.Sts2.Core.Multiplayer.Transport;

namespace PwA.Coop.Transport;

/// <summary>
/// broker 信封里承载游戏报文的格式：
/// [1 字节 mode][4 字节 channel][游戏原始字节]。
/// 游戏侧的 NetHost/NetClient 只需要字节数组，所以这里把传输模式一起带过去。
/// </summary>
internal static class BrokerWire
{
    public const int HeaderLength = 5;

    public static byte[] Pack(byte[] bytes, int length, NetTransferMode mode, int channel)
    {
        var buffer = new byte[HeaderLength + length];
        buffer[0] = (byte)mode;
        BinaryPrimitives.WriteInt32BigEndian(buffer.AsSpan(1, 4), channel);
        Array.Copy(bytes, 0, buffer, HeaderLength, length);
        return buffer;
    }

    public static (NetTransferMode Mode, int Channel, byte[] Data) Unpack(byte[] payload)
    {
        if (payload.Length < HeaderLength)
        {
            return (NetTransferMode.Reliable, 0, Array.Empty<byte>());
        }

        var mode = (NetTransferMode)payload[0];
        var channel = BinaryPrimitives.ReadInt32BigEndian(payload.AsSpan(1, 4));
        var data = new byte[payload.Length - HeaderLength];
        Array.Copy(payload, HeaderLength, data, 0, data.Length);
        return (mode, channel, data);
    }
}
