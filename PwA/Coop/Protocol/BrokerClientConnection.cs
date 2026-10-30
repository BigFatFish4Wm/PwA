using System.Net.Sockets;

namespace PwA.Coop.Protocol;

public sealed class BrokerClientConnection : IAsyncDisposable
{
    private readonly TcpClient _client;
    private readonly NetworkStream _stream;
    private readonly List<BrokerClientRegistrationDto> _connectedPeers = [];
    private readonly object _connectedPeersGate = new();

    private BrokerClientConnection(TcpClient client)
    {
        _client = client;
        _stream = client.GetStream();
    }

    public static async Task<BrokerClientConnection> ConnectAsync(
        BrokerClientConfig config,
        string clientId,
        CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(clientId))
        {
            throw new ArgumentException("Client id must not be blank.", nameof(clientId));
        }

        var client = new TcpClient();
        await client.ConnectAsync(config.Host, config.Port, cancellationToken);
        var connection = new BrokerClientConnection(client);

        await BrokerFrameCodec.WriteAsync(
            connection._stream,
            BrokerTransportMessage.ForRegistration(
                new BrokerClientRegistrationDto(clientId, config.Role, config.ClientIndex)),
            cancellationToken);

        var accepted = await BrokerFrameCodec.ReadAsync(connection._stream, cancellationToken);
        if (accepted?.Kind == BrokerTransportMessageKind.RegistrationRejected)
        {
            await connection.DisposeAsync();
            throw new InvalidDataException(
                $"Broker registration rejected: {accepted.RegistrationRejected?.Reason ?? "unknown reason"}");
        }

        if (accepted?.Kind != BrokerTransportMessageKind.RegistrationAccepted
            || accepted.RegistrationAccepted?.ClientId != clientId
            || accepted.RegistrationAccepted.SessionId != config.SessionId
            || accepted.RegistrationAccepted.ProtocolVersion != BrokerProtocol.CurrentVersion)
        {
            await connection.DisposeAsync();
            throw new InvalidDataException("Broker did not accept registration for the requested client.");
        }

        connection.SetConnectedPeers(accepted.RegistrationAccepted.ConnectedPeers);
        return connection;
    }

    public event Action<BrokerClientRegistrationDto>? PeerRegistered;

    public IReadOnlyList<BrokerClientRegistrationDto> ConnectedPeers
    {
        get
        {
            lock (_connectedPeersGate)
            {
                return _connectedPeers.ToArray();
            }
        }
    }

    public async Task SendEnvelopeAsync(BrokerEnvelope envelope, CancellationToken cancellationToken)
    {
        await BrokerFrameCodec.WriteAsync(
            _stream,
            BrokerTransportMessage.ForEnvelope(envelope),
            cancellationToken);
    }

    public async Task<BrokerEnvelope?> ReadEnvelopeAsync(CancellationToken cancellationToken)
    {
        while (true)
        {
            var message = await BrokerFrameCodec.ReadAsync(_stream, cancellationToken);
            if (message is null)
            {
                return null;
            }

            if (message.Kind == BrokerTransportMessageKind.Envelope)
            {
                return message.Envelope
                    ?? throw new InvalidDataException("Broker envelope message did not include an envelope.");
            }

            if (message.Kind == BrokerTransportMessageKind.PeerRegistered)
            {
                AddConnectedPeer(message.PeerRegistration
                    ?? throw new InvalidDataException("Broker peer registration message did not include peer data."));
            }
        }
    }

    public ValueTask DisposeAsync()
    {
        _stream.Dispose();
        _client.Dispose();
        return ValueTask.CompletedTask;
    }

    private void SetConnectedPeers(IReadOnlyList<BrokerClientRegistrationDto> registrations)
    {
        lock (_connectedPeersGate)
        {
            _connectedPeers.Clear();
            _connectedPeers.AddRange(registrations);
        }
    }

    private void AddConnectedPeer(BrokerClientRegistrationDto registration)
    {
        var added = false;
        lock (_connectedPeersGate)
        {
            if (_connectedPeers.All(peer => !string.Equals(peer.ClientId, registration.ClientId, StringComparison.Ordinal)))
            {
                _connectedPeers.Add(registration);
                added = true;
            }
        }

        if (added)
        {
            PeerRegistered?.Invoke(registration);
        }
    }
}
