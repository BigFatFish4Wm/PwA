using System.Buffers.Binary;
using System.Text.Json;

namespace PwA.Coop.Protocol;

public static class BrokerFrameCodec
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);

    public static async Task WriteAsync(
        Stream stream,
        BrokerTransportMessage message,
        CancellationToken cancellationToken)
    {
        var payload = JsonSerializer.SerializeToUtf8Bytes(message, JsonOptions);
        var lengthPrefix = new byte[4];
        BinaryPrimitives.WriteInt32BigEndian(lengthPrefix, payload.Length);

        await stream.WriteAsync(lengthPrefix, cancellationToken);
        await stream.WriteAsync(payload, cancellationToken);
        await stream.FlushAsync(cancellationToken);
    }

    public static async Task<BrokerTransportMessage?> ReadAsync(
        Stream stream,
        CancellationToken cancellationToken)
    {
        var lengthPrefix = new byte[4];
        var prefixBytes = await ReadExactlyOrEndAsync(stream, lengthPrefix, cancellationToken);
        if (prefixBytes == 0)
        {
            return null;
        }

        if (prefixBytes < lengthPrefix.Length)
        {
            throw new EndOfStreamException("Broker frame ended during length prefix.");
        }

        var length = BinaryPrimitives.ReadInt32BigEndian(lengthPrefix);
        if (length <= 0)
        {
            throw new InvalidDataException("Broker frame length must be positive.");
        }

        var payload = new byte[length];
        var payloadBytes = await ReadExactlyOrEndAsync(stream, payload, cancellationToken);
        if (payloadBytes < length)
        {
            throw new EndOfStreamException("Broker frame ended during payload.");
        }

        return JsonSerializer.Deserialize<BrokerTransportMessage>(payload, JsonOptions)
            ?? throw new InvalidDataException("Broker frame payload did not contain a message.");
    }

    private static async Task<int> ReadExactlyOrEndAsync(
        Stream stream,
        byte[] buffer,
        CancellationToken cancellationToken)
    {
        var totalRead = 0;
        while (totalRead < buffer.Length)
        {
            var read = await stream.ReadAsync(buffer.AsMemory(totalRead), cancellationToken);
            if (read == 0)
            {
                return totalRead;
            }

            totalRead += read;
        }

        return totalRead;
    }
}

