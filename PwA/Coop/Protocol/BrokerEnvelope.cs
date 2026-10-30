namespace PwA.Coop.Protocol;

public sealed record BrokerEnvelope(
    string SessionId,
    string SourceClientId,
    string? TargetClientId,
    string MessageType,
    byte[] Payload,
    long Sequence)
{
    public static BrokerEnvelope Broadcast(
        string sessionId,
        string sourceClientId,
        string messageType,
        IReadOnlyList<byte> payload,
        long sequence)
    {
        return new BrokerEnvelope(
            sessionId,
            sourceClientId,
            TargetClientId: null,
            messageType,
            payload.ToArray(),
            sequence);
    }
}

