using System.Collections.Concurrent;
using MegaCrit.Sts2.Core.Multiplayer.Transport;
using PwA.Coop.Protocol;

namespace PwA.Coop.Transport;

/// <summary>
/// 一条到 broker 中继的连接：注册身份、收发信封、跟踪对端。
/// 收包只入队，由游戏主线程的 Update() 取走，避免 Godot 跨线程访问。
/// </summary>
internal sealed class BrokerLink : IAsyncDisposable
{
    public const string NetMessageType = "pwa.net";
    public const string PeerLeftMessageType = "pwa.peer.left";

    private readonly BrokerClientConnection _connection;
    private readonly ConcurrentQueue<BrokerEnvelope> _inbound = new();
    private readonly CancellationTokenSource _cancellation = new();
    private readonly Action<string>? _log;
    private readonly Task _receiveLoop;

    private BrokerLink(BrokerClientConnection connection, string clientId, string sessionId, Action<string>? log)
    {
        _connection = connection;
        ClientId = clientId;
        SessionId = sessionId;
        _log = log;
        _receiveLoop = Task.Run(() => ReceiveLoopAsync(_cancellation.Token));
    }

    public string ClientId { get; }

    public string SessionId { get; }

    public event Action<int>? PeerJoined;

    public event Action<string>? PeerLeft;

    public bool IsConnected => !_cancellation.IsCancellationRequested;

    public static async Task<BrokerLink> ConnectAsync(
        BrokerClientConfig config,
        string clientId,
        Action<string>? log,
        CancellationToken cancellationToken)
    {
        var connection = await BrokerClientConnection.ConnectAsync(config, clientId, cancellationToken);
        var link = new BrokerLink(connection, clientId, config.SessionId, log);

        foreach (var peer in connection.ConnectedPeers)
        {
            if (!string.Equals(peer.ClientId, clientId, StringComparison.Ordinal))
            {
                link.PeerJoined?.Invoke(peer.ClientIndex);
            }
        }

        connection.PeerRegistered += registration =>
        {
            log?.Invoke($"对端注册: {registration.ClientId} index={registration.ClientIndex}");
            if (!string.Equals(registration.ClientId, clientId, StringComparison.Ordinal))
            {
                link.PeerJoined?.Invoke(registration.ClientIndex);
            }
        };

        log?.Invoke($"已连上 broker {config.Endpoint}（clientId={clientId} session={config.SessionId}）");
        return link;
    }

    public void SendBroadcast(byte[] payload)
    {
        _ = SendAsync(BrokerEnvelope.Broadcast(SessionId, ClientId, NetMessageType, payload, NextSequence()));
    }

    public void SendTo(string targetClientId, byte[] payload)
    {
        _ = SendAsync(new BrokerEnvelope(SessionId, ClientId, targetClientId, NetMessageType, payload, NextSequence()));
    }

    public bool TryDequeue(out BrokerEnvelope envelope)
    {
        return _inbound.TryDequeue(out envelope!);
    }

    private static long _sequence;

    private static long NextSequence() => Interlocked.Increment(ref _sequence);

    private async Task SendAsync(BrokerEnvelope envelope)
    {
        try
        {
            await _connection.SendEnvelopeAsync(envelope, CancellationToken.None);
        }
        catch (Exception exception)
        {
            _log?.Invoke($"发送失败: {exception.Message}");
        }
    }

    private async Task ReceiveLoopAsync(CancellationToken cancellationToken)
    {
        while (!cancellationToken.IsCancellationRequested)
        {
            BrokerEnvelope? envelope;
            try
            {
                envelope = await _connection.ReadEnvelopeAsync(cancellationToken);
            }
            catch (Exception exception)
            {
                if (!cancellationToken.IsCancellationRequested)
                {
                    _log?.Invoke($"接收中断: {exception.Message}");
                }

                return;
            }

            if (envelope == null)
            {
                return;
            }

            if (string.Equals(envelope.MessageType, PeerLeftMessageType, StringComparison.Ordinal))
            {
                var leftClientId = System.Text.Encoding.UTF8.GetString(envelope.Payload);
                PeerLeft?.Invoke(leftClientId);
                continue;
            }

            if (string.Equals(envelope.MessageType, NetMessageType, StringComparison.Ordinal))
            {
                _inbound.Enqueue(envelope);
            }
        }
    }

    public async ValueTask DisposeAsync()
    {
        try
        {
            _cancellation.Cancel();
        }
        catch
        {
        }

        try
        {
            await _connection.DisposeAsync();
        }
        catch
        {
        }

        try
        {
            await _receiveLoop.WaitAsync(TimeSpan.FromSeconds(1));
        }
        catch
        {
        }
    }
}
