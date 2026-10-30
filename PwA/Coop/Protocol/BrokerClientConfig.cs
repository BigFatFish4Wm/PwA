namespace PwA.Coop.Protocol;

/// <summary>这个窗口在 broker 会话里的身份与地址。</summary>
public sealed record BrokerClientConfig(
    BrokerClientRole Role,
    int ClientIndex,
    string Host,
    int Port,
    string SessionId)
{
    public string Endpoint => $"{Host}:{Port}";

    public string ClientId => $"client-{ClientIndex}";

    public BrokerClientConfig Validate()
    {
        if (ClientIndex < 0)
        {
            throw new FormatException("clientIndex 不能是负数");
        }

        if (Port <= 0 || Port > 65535)
        {
            throw new FormatException($"endpoint 端口非法: {Port}");
        }

        if (string.IsNullOrWhiteSpace(SessionId))
        {
            throw new FormatException("sessionId 不能为空");
        }

        return this;
    }
}