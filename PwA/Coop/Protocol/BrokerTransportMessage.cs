using System.Text.Json.Serialization;

namespace PwA.Coop.Protocol;

public enum BrokerTransportMessageKind
{
    Registration,
    RegistrationAccepted,
    RegistrationRejected,
    Envelope,
    PeerRegistered
}

public sealed record BrokerTransportMessage(
    BrokerTransportMessageKind Kind,
    BrokerClientRegistrationDto? Registration,
    BrokerRegistrationAccepted? RegistrationAccepted,
    BrokerRegistrationRejected? RegistrationRejected,
    BrokerEnvelope? Envelope,
    BrokerClientRegistrationDto? PeerRegistration)
{
    public static BrokerTransportMessage ForRegistration(BrokerClientRegistrationDto registration)
    {
        return new BrokerTransportMessage(
            BrokerTransportMessageKind.Registration,
            registration,
            RegistrationAccepted: null,
            RegistrationRejected: null,
            Envelope: null,
            PeerRegistration: null);
    }

    public static BrokerTransportMessage ForRegistrationAccepted(
        string clientId,
        string sessionId,
        IReadOnlyList<BrokerClientRegistrationDto>? connectedPeers = null)
    {
        return new BrokerTransportMessage(
            BrokerTransportMessageKind.RegistrationAccepted,
            Registration: null,
            new BrokerRegistrationAccepted(clientId, sessionId, connectedPeers),
            RegistrationRejected: null,
            Envelope: null,
            PeerRegistration: null);
    }

    public static BrokerTransportMessage ForRegistrationRejected(string reason)
    {
        return new BrokerTransportMessage(
            BrokerTransportMessageKind.RegistrationRejected,
            Registration: null,
            RegistrationAccepted: null,
            new BrokerRegistrationRejected(reason),
            Envelope: null,
            PeerRegistration: null);
    }

    public static BrokerTransportMessage ForEnvelope(BrokerEnvelope envelope)
    {
        return new BrokerTransportMessage(
            BrokerTransportMessageKind.Envelope,
            Registration: null,
            RegistrationAccepted: null,
            RegistrationRejected: null,
            envelope,
            PeerRegistration: null);
    }

    public static BrokerTransportMessage ForPeerRegistered(BrokerClientRegistrationDto peerRegistration)
    {
        return new BrokerTransportMessage(
            BrokerTransportMessageKind.PeerRegistered,
            Registration: null,
            RegistrationAccepted: null,
            RegistrationRejected: null,
            Envelope: null,
            peerRegistration);
    }
}

public sealed record BrokerClientRegistrationDto(
    string ClientId,
    BrokerClientRole Role,
    int ClientIndex,
    int ProtocolVersion = BrokerProtocol.CurrentVersion);

public sealed record BrokerRegistrationAccepted
{
    [JsonConstructor]
    public BrokerRegistrationAccepted(
        string clientId,
        string sessionId,
        IReadOnlyList<BrokerClientRegistrationDto>? connectedPeers = null,
        int protocolVersion = BrokerProtocol.CurrentVersion)
    {
        ClientId = clientId;
        SessionId = sessionId;
        ConnectedPeers = connectedPeers ?? [];
        ProtocolVersion = protocolVersion;
    }

    public string ClientId { get; init; }

    public string SessionId { get; init; }

    public IReadOnlyList<BrokerClientRegistrationDto> ConnectedPeers { get; init; }

    public int ProtocolVersion { get; init; }
}

public sealed record BrokerRegistrationRejected(string Reason);
