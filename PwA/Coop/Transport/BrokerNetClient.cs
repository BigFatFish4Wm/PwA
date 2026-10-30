using MegaCrit.Sts2.Core.Entities.Multiplayer;
using MegaCrit.Sts2.Core.Multiplayer.Transport;
using PwA.Coop.Protocol;

namespace PwA.Coop.Transport;

/// <summary>
/// 用 broker 中继替代 ENet/Steam 的 client 传输。HostNetId 固定 1（与 ENet 一致）。
/// </summary>
internal sealed class BrokerNetClient : NetClient
{
    private readonly BrokerClientConfig _config;
    private readonly Action<string>? _log;
    private BrokerLink? _link;
    private bool _connected;
    private int _sendCount;
    private int _recvCount;

    public BrokerNetClient(INetClientHandler handler, BrokerClientConfig config, Action<string>? log = null)
        : base(handler)
    {
        _config = config;
        _log = log;
    }

    public override bool IsConnected => _connected && _link?.IsConnected == true;

    public override ulong NetId => (ulong)(_config.ClientIndex + 1);

    public override ulong HostNetId => 1UL;

    public async Task<NetErrorInfo?> ConnectAsync(CancellationToken cancellationToken)
    {
        try
        {
            var link = await BrokerLink.ConnectAsync(_config, $"client-{_config.ClientIndex}", _log, cancellationToken);
            link.PeerLeft += clientId =>
            {
                if (string.Equals(clientId, "client-0", StringComparison.Ordinal))
                {
                    _connected = false;
                    _handler.OnDisconnectedFromHost(HostNetId, new NetErrorInfo(NetError.UnknownNetworkError, selfInitiated: false));
                }
            };
            _link = link;
            _connected = true;
            BrokerTransportPump.Register(Update);
            _log?.Invoke($"BrokerNetClient 已连上 broker（netId={NetId}）");
            try
            {
                _log?.Invoke($"调用 OnConnectedToHost（handler={_handler.GetType().Name}）");
                _handler.OnConnectedToHost();
                HandshakeKick.WriteForClient(_handler, HostNetId);
                _log?.Invoke("OnConnectedToHost 返回");
            }
            catch (Exception exception)
            {
                _log?.Invoke($"OnConnectedToHost 抛异常: {exception}");
                throw;
            }

            return null;
        }
        catch (Exception exception)
        {
            _log?.Invoke($"BrokerNetClient 连接失败: {exception.Message}");
            return new NetErrorInfo(NetError.UnknownNetworkError, selfInitiated: true);
        }
    }

    public override void SendMessageToHost(byte[] bytes, int length, NetTransferMode mode, int channel = 0)
    {
        CoopLog.Write($"client->host len={length} mode={mode} ch={channel}");
        _link?.SendTo("client-0", BrokerWire.Pack(bytes, length, mode, channel));
    }

    public override void DisconnectFromHost(NetError reason, bool now = false)
    {
        _connected = false;
        _link?.DisposeAsync().AsTask().Wait(TimeSpan.FromSeconds(1));
        _handler.OnDisconnectedFromHost(HostNetId, new NetErrorInfo(reason, selfInitiated: true));
    }

    public override string? GetRawLobbyIdentifier() => $"pwa-broker:{_config.SessionId}";

    public override void Update()
    {
        if (_link == null)
        {
            return;
        }

        while (_link.TryDequeue(out var envelope))
        {
            var (mode, channel, data) = BrokerWire.Unpack(envelope.Payload);
            if (++_recvCount <= 6) CoopLog.Write($"client<-host len={data.Length} mode={mode} ch={channel}");
            _handler.OnPacketReceived(HostNetId, data, mode, channel);
        }
    }
}
