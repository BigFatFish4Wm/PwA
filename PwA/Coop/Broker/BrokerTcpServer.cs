using PwA.Coop.Protocol;
using System.Net;
using System.Net.Sockets;

namespace PwA.Coop.Broker;

public sealed class BrokerTcpServer : IAsyncDisposable
{
    private readonly InMemoryBrokerSession _session;
    private readonly TcpListener _listener;
    private readonly CancellationTokenSource _shutdown = new();
    private readonly Dictionary<string, BrokerClientPipe> _pipesByClientId = new(StringComparer.Ordinal);
    private readonly Dictionary<string, List<BrokerEnvelope>> _mailboxesByClientId = new(StringComparer.Ordinal);
    private readonly List<BrokerEnvelope> _pendingHostEnvelopes = [];
    private readonly object _stateLock = new();
    private readonly Action<string>? _log;
    private readonly string? _requiredToken;
    private Task? _acceptLoop;

    public BrokerTcpServer(string sessionId, IPAddress address, int port, Action<string>? log = null, string? requiredToken = null)
    {
        _session = new InMemoryBrokerSession(sessionId);
        _requiredToken = string.IsNullOrWhiteSpace(requiredToken) ? null : requiredToken;
        _listener = new TcpListener(address, port);
        _log = log;
    }

    public int Port
    {
        get
        {
            if (_listener.LocalEndpoint is not IPEndPoint endpoint)
            {
                throw new InvalidOperationException("Broker TCP server has not started.");
            }

            return endpoint.Port;
        }
    }

    public Task StartAsync(CancellationToken cancellationToken)
    {
        _listener.Start();
        _acceptLoop = Task.Run(() => AcceptLoopAsync(_shutdown.Token), cancellationToken);
        return Task.CompletedTask;
    }

    public async ValueTask DisposeAsync()
    {
        await _shutdown.CancelAsync();
        _listener.Stop();

        lock (_stateLock)
        {
            foreach (var pipe in _pipesByClientId.Values)
            {
                pipe.Dispose();
            }

            _pipesByClientId.Clear();
            _mailboxesByClientId.Clear();
            _pendingHostEnvelopes.Clear();
        }

        if (_acceptLoop is not null)
        {
            try
            {
                await _acceptLoop;
            }
            catch (OperationCanceledException)
            {
            }
            catch (SocketException)
            {
            }
            catch (ObjectDisposedException)
            {
            }
        }

        _shutdown.Dispose();
    }

    private async Task AcceptLoopAsync(CancellationToken cancellationToken)
    {
        while (!cancellationToken.IsCancellationRequested)
        {
            var client = await _listener.AcceptTcpClientAsync(cancellationToken);
            _ = Task.Run(() => HandleClientAsync(client, cancellationToken), cancellationToken);
        }
    }

    private async Task HandleClientAsync(TcpClient client, CancellationToken cancellationToken)
    {
        using var clientLease = client;
        var stream = client.GetStream();
        string? clientId = null;

        try
        {
            while (!cancellationToken.IsCancellationRequested)
            {
                var message = await BrokerFrameCodec.ReadAsync(stream, cancellationToken);
                if (message is null)
                {
                    return;
                }

                if (message.Kind == BrokerTransportMessageKind.Registration)
                {
                    if (message.Registration is null)
                    {
                        throw new InvalidDataException("Registration message did not include registration data.");
                    }

                    var registration = BrokerClientRegistration.FromDto(message.Registration);
                    if (_requiredToken != null && !string.Equals(message.Registration.Token, _requiredToken, StringComparison.Ordinal))
                    {
                        var tokenReason = "Broker registration rejected: token mismatch.";
                        _log?.Invoke($"Broker registration rejected: client={message.Registration.ClientId} reason=token");
                        await BrokerFrameCodec.WriteAsync(stream, BrokerTransportMessage.ForRegistrationRejected(tokenReason), cancellationToken);
                        return;
                    }
                    if (message.Registration.ProtocolVersion != BrokerProtocol.CurrentVersion)
                    {
                        var reason = $"Unsupported broker protocol version {message.Registration.ProtocolVersion}; expected {BrokerProtocol.CurrentVersion}.";
                        _log?.Invoke($"Broker registration rejected: client={message.Registration.ClientId} reason={reason}");
                        await BrokerFrameCodec.WriteAsync(
                            stream,
                            BrokerTransportMessage.ForRegistrationRejected(reason),
                            cancellationToken);
                        return;
                    }
                    IReadOnlyList<BrokerRoute> ready;
                    IReadOnlyList<BrokerClientRegistrationDto> connectedPeers;
                    IReadOnlyList<BrokerTransportRoute> peerNotifications;
                    lock (_stateLock)
                    {
                        _session.Register(registration);
                        clientId = registration.ClientId;
                        _pipesByClientId[clientId] = new BrokerClientPipe(stream);
                        ready = CollectReadyAfterRegistration(registration);
                        var existingClients = _session.Clients
                            .Where(client => !string.Equals(client.ClientId, clientId, StringComparison.Ordinal))
                            .ToArray();
                        connectedPeers = existingClients
                            .Select(client => client.ToDto())
                            .ToArray();
                        peerNotifications = existingClients
                            .Select(client => new BrokerTransportRoute(
                                client.ClientId,
                                BrokerTransportMessage.ForPeerRegistered(registration.ToDto())))
                            .ToArray();
                    }

                    _log?.Invoke($"客户端注册: {clientId} role={registration.Role} index={registration.ClientIndex}，通知 {peerNotifications.Count} 个已连接窗口");
                    await BrokerFrameCodec.WriteAsync(
                        stream,
                        BrokerTransportMessage.ForRegistrationAccepted(clientId, _session.SessionId, connectedPeers),
                        cancellationToken);
                    await DeliverTransportMessagesAsync(peerNotifications, cancellationToken);
                    await DeliverAsync(ready, cancellationToken);
                    continue;
                }

                if (message.Envelope is null)
                {
                    throw new InvalidDataException("Envelope message did not include an envelope.");
                }

                var routes = RouteOrQueue(message.Envelope);
                _log?.Invoke($"转发 {message.Envelope.MessageType} from={message.Envelope.SourceClientId} to={string.Join(",", routes.Select(route => route.TargetClientId))}");
                await DeliverAsync(routes, cancellationToken);
            }
        }
        finally
        {
            if (clientId is not null)
            {
                lock (_stateLock)
                {
                    if (_pipesByClientId.Remove(clientId, out var pipe))
                    {
                        pipe.Dispose();
                    }

                    DropPendingForDisconnectedClient(clientId);
                    _session.Unregister(clientId);
                    BroadcastPeerLeft(clientId);

                }
            }
        }
    }

    /// <summary>通知其它窗口「某个客户端走了」，host 才能及时把对端从 ConnectedPeers 里移除。</summary>
    private void BroadcastPeerLeft(string clientId)
    {
        var payload = System.Text.Encoding.UTF8.GetBytes(clientId);
        var envelope = new BrokerEnvelope(_session.SessionId, "broker", TargetClientId: null, "pwa.peer.left", payload, 0);
        var routes = _session.Clients
            .Where(client => !string.Equals(client.ClientId, clientId, StringComparison.Ordinal))
            .Select(client => new BrokerRoute(client.ClientId, envelope))
            .ToList();

        if (routes.Count > 0)
        {
            _ = DeliverAsync(routes, CancellationToken.None);
        }
    }

    private IReadOnlyList<BrokerRoute> RouteOrQueue(BrokerEnvelope envelope)
    {
        lock (_stateLock)
        {
            var source = _session.FindClient(envelope.SourceClientId)
                ?? throw new InvalidOperationException($"source client '{envelope.SourceClientId}' is not registered.");


            if (envelope.TargetClientId is not null)
            {
                return RouteDirectOrQueue(envelope.TargetClientId, envelope);
            }

            if (source.Role == BrokerClientRole.Client)
            {
                if (_session.HostClientId is null)
                {
                    _pendingHostEnvelopes.Add(envelope);
                    _log?.Invoke(
                        $"Broker queued envelope: target=host source={envelope.SourceClientId} messageType={envelope.MessageType} sequence={envelope.Sequence} reason=host is not registered.");
                    return [];
                }

                return RouteDirectOrQueue(_session.HostClientId, envelope);
            }

            var deliver = new List<BrokerRoute>();
            foreach (var route in _session.Route(envelope))
            {
                deliver.AddRange(RouteDirectOrQueue(route.TargetClientId, route.Envelope));
            }

            return deliver;
        }
    }

    private IReadOnlyList<BrokerRoute> RouteDirectOrQueue(string targetClientId, BrokerEnvelope envelope)
    {
        if (_pipesByClientId.ContainsKey(targetClientId))
        {
            var route = new BrokerRoute(targetClientId, envelope);
            LogRelease(route, "immediate");
            return [route];
        }

        if (!_mailboxesByClientId.TryGetValue(targetClientId, out var mailbox))
        {
            mailbox = [];
            _mailboxesByClientId[targetClientId] = mailbox;
        }

        mailbox.Add(envelope);
        _log?.Invoke(
            $"Broker queued envelope: target={targetClientId} source={envelope.SourceClientId} messageType={envelope.MessageType} sequence={envelope.Sequence} reason=target is not connected.");
        return [];
    }

    private IReadOnlyList<BrokerRoute> CollectReadyAfterRegistration(BrokerClientRegistration registration)
    {
        var ready = new List<BrokerRoute>();
        if (_mailboxesByClientId.Remove(registration.ClientId, out var mailbox))
        {
            foreach (var envelope in mailbox)
            {
                var route = new BrokerRoute(registration.ClientId, envelope);
                ready.Add(route);
                LogRelease(route, "registration");
            }
        }

        if (registration.Role == BrokerClientRole.Host)
        {
            foreach (var envelope in _pendingHostEnvelopes)
            {
                var route = new BrokerRoute(registration.ClientId, envelope);
                ready.Add(route);
                LogRelease(route, "host-registration");
            }

            _pendingHostEnvelopes.Clear();
        }

        return ready;
    }

    private async Task DeliverAsync(IReadOnlyList<BrokerRoute> routes, CancellationToken cancellationToken)
    {
        foreach (var route in routes)
        {
            BrokerClientPipe? pipe;
            lock (_stateLock)
            {
                _pipesByClientId.TryGetValue(route.TargetClientId, out pipe);
            }

            if (pipe is null)
            {
                _log?.Invoke(
                    $"Broker dropped envelope: target={route.TargetClientId} source={route.Envelope.SourceClientId} messageType={route.Envelope.MessageType} sequence={route.Envelope.Sequence} reason=target disconnected before delivery.");
                continue;
            }

            await pipe.WriteAsync(
                BrokerTransportMessage.ForEnvelope(route.Envelope),
                cancellationToken);
        }
    }

    private async Task DeliverTransportMessagesAsync(
        IReadOnlyList<BrokerTransportRoute> routes,
        CancellationToken cancellationToken)
    {
        foreach (var route in routes)
        {
            BrokerClientPipe? pipe;
            lock (_stateLock)
            {
                _pipesByClientId.TryGetValue(route.TargetClientId, out pipe);
            }

            if (pipe is null)
            {
                _log?.Invoke(
                    $"Broker dropped transport message: target={route.TargetClientId} kind={route.Message.Kind} reason=target disconnected before delivery.");
                continue;
            }

            await pipe.WriteAsync(route.Message, cancellationToken);
        }
    }

    private void DropPendingForDisconnectedClient(string clientId)
    {
        if (_mailboxesByClientId.Remove(clientId, out var targetMailbox))
        {
            foreach (var envelope in targetMailbox)
            {
                _log?.Invoke(
                    $"Broker dropped envelope: target={clientId} source={envelope.SourceClientId} messageType={envelope.MessageType} sequence={envelope.Sequence} reason=target disconnected.");
            }
        }

        for (var index = _pendingHostEnvelopes.Count - 1; index >= 0; index--)
        {
            var envelope = _pendingHostEnvelopes[index];
            if (!string.Equals(envelope.SourceClientId, clientId, StringComparison.Ordinal))
            {
                continue;
            }

            _pendingHostEnvelopes.RemoveAt(index);
            _log?.Invoke(
                $"Broker dropped envelope: source={clientId} target=host messageType={envelope.MessageType} sequence={envelope.Sequence} reason=source disconnected.");
        }

        foreach (var mailbox in _mailboxesByClientId.Values)
        {
            for (var index = mailbox.Count - 1; index >= 0; index--)
            {
                var envelope = mailbox[index];
                if (!string.Equals(envelope.SourceClientId, clientId, StringComparison.Ordinal))
                {
                    continue;
                }

                mailbox.RemoveAt(index);
                _log?.Invoke(
                    $"Broker dropped envelope: source={clientId} target=pending messageType={envelope.MessageType} sequence={envelope.Sequence} reason=source disconnected.");
            }
        }
    }

    private void LogRelease(BrokerRoute route, string trigger)
    {
        _log?.Invoke(
            $"Broker released envelope: target={route.TargetClientId} source={route.Envelope.SourceClientId} messageType={route.Envelope.MessageType} sequence={route.Envelope.Sequence} trigger={trigger}.");
    }

    private sealed record BrokerTransportRoute(
        string TargetClientId,
        BrokerTransportMessage Message);

    private sealed class BrokerClientPipe : IDisposable
    {
        private readonly NetworkStream _stream;
        private readonly SemaphoreSlim _writeGate = new(1, 1);

        public BrokerClientPipe(NetworkStream stream)
        {
            _stream = stream;
        }

        public async Task WriteAsync(BrokerTransportMessage message, CancellationToken cancellationToken)
        {
            await _writeGate.WaitAsync(cancellationToken);
            try
            {
                await BrokerFrameCodec.WriteAsync(_stream, message, cancellationToken);
            }
            finally
            {
                _writeGate.Release();
            }
        }

        public void Dispose()
        {
            _writeGate.Dispose();
            _stream.Dispose();
        }
    }
}
