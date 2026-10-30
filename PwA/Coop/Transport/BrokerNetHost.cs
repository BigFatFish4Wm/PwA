using MegaCrit.Sts2.Core.Entities.Multiplayer;
using MegaCrit.Sts2.Core.Multiplayer.Transport;
using PwA.Coop.Protocol;

namespace PwA.Coop.Transport;

/// <summary>
/// 用 broker 中继替代 ENet/Steam 的 host 传输。只负责字节搬运，
/// 握手、消息总线、版本校验仍然由游戏自己的 NetHostGameService 完成。
/// </summary>
internal sealed class BrokerNetHost : NetHost
{
    private readonly BrokerClientConfig _config;
    private readonly Action<string>? _log;
    private readonly HashSet<ulong> _peers = new();

    private BrokerLink? _link;
    private int _sendCount;
    private int _recvCount;
    private bool _hostClosed;

    public BrokerNetHost(INetHostHandler handler, BrokerClientConfig config, Action<string>? log = null)
        : base(handler)
    {
        _config = config;
        _log = log;
    }

    // 注意：SetHostIsClosed 只是「大厅不再接新人」，不代表传输断了。混在一起会让开跑后所有消息都发不出去。
    public override bool IsConnected => _link?.IsConnected == true;

    public override IEnumerable<ulong> ConnectedPeerIds => _peers.ToArray();

    public override ulong NetId => 1UL;

    public void Start()
    {
        // 不能阻塞游戏主线程：连接与注册放到后台，IsConnected 在连上后才变 true。
        _ = Task.Run(async () =>
        {
            try
            {
                var link = await BrokerLink.ConnectAsync(_config, $"client-{_config.ClientIndex}", _log, CancellationToken.None);
                link.PeerJoined += OnPeerJoined;
                link.PeerLeft += OnPeerLeft;
                _link = link;
                _log?.Invoke($"BrokerNetHost 已就绪（endpoint={_config.Endpoint}）");
                BrokerTransportPump.Register(Update);
            }
            catch (Exception exception)
            {
                _log?.Invoke($"BrokerNetHost 连接失败: {exception.Message}");
            }
        });
    }

    private void OnPeerJoined(int clientIndex)
    {
        var peerId = (ulong)(clientIndex + 1);
        if (!_peers.Add(peerId))
        {
            return;
        }

        _log?.Invoke($"对端加入: clientIndex={clientIndex} netId={peerId}");
        // 对端回调必须在游戏主线程上跑（握手管理器会碰 Godot 侧状态）。
        _ = Game.GameThread.InvokeAsync(() =>
        {
            try
            {
                _handler.OnPeerConnected(peerId);
                HandshakeKick.WriteForHost(_handler, peerId);
            }
            catch (Exception exception)
            {
                _log?.Invoke($"OnPeerConnected 失败: {exception}");
            }
        });
    }

    private void OnPeerLeft(string clientId)
    {
        var index = clientId.StartsWith("client-", StringComparison.Ordinal)
            && int.TryParse(clientId.Substring("client-".Length), out var parsed)
            ? parsed
            : -1;
        if (index < 0)
        {
            return;
        }

        var peerId = (ulong)(index + 1);
        if (_peers.Remove(peerId))
        {
            _handler.OnPeerDisconnected(peerId, new NetErrorInfo(NetError.UnknownNetworkError, selfInitiated: false));
        }
    }

    public override void SendMessageToAll(byte[] bytes, int length, NetTransferMode mode, int channel)
    {
        if (++_sendCount <= 20) CoopLog.Write($"host->all len={length} mode={mode} ch={channel}");
        _link?.SendBroadcast(BrokerWire.Pack(bytes, length, mode, channel));
    }

    public override void SendMessageToClient(ulong peerId, byte[] bytes, int length, NetTransferMode mode, int channel)
    {
        var index = (int)peerId - 1;
        if (index < 1)
        {
            return;
        }

        if (++_sendCount <= 20) CoopLog.Write($"host->{peerId} len={length} mode={mode} ch={channel}");
        _link?.SendTo($"client-{index}", BrokerWire.Pack(bytes, length, mode, channel));
    }

    public override void DisconnectClient(ulong peerId, NetError reason, bool now)
    {
        if (_peers.Remove(peerId))
        {
            _handler.OnPeerDisconnected(peerId, new NetErrorInfo(reason, selfInitiated: true));
        }
    }

    public override void SetHostIsClosed(bool isClosed)
    {
        _hostClosed = isClosed;
    }

    public override void StopHost(NetError reason, bool now)
    {
        _hostClosed = true;
        _link?.DisposeAsync().AsTask().Wait(TimeSpan.FromSeconds(1));
        _handler.OnDisconnected(new NetErrorInfo(reason, selfInitiated: true));
    }

    public override string? GetRawLobbyIdentifier() => $"pwa-broker:{_config.SessionId}";

    private int _updateLogged;

    public override void Update()
    {
        if (_link == null)
        {
            return;
        }


        while (_link.TryDequeue(out var envelope))
        {
            var index = envelope.SourceClientId.StartsWith("client-", StringComparison.Ordinal)
                && int.TryParse(envelope.SourceClientId.Substring("client-".Length), out var parsed)
                ? parsed
                : -1;
            if (index < 0)
            {
                continue;
            }

            var (mode, channel, data) = BrokerWire.Unpack(envelope.Payload);
            if (++_recvCount <= 6) CoopLog.Write($"host<-{index + 1} len={data.Length} mode={mode} ch={channel}");
            _handler.OnPacketReceived((ulong)(index + 1), data, mode, channel);
        }
    }
}
