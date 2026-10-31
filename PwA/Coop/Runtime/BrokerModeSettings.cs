namespace PwA.Coop.Runtime;

/// <summary>
/// 联机（broker）模式配置：全部来自环境变量，缺省关闭。
///   PWA_COOP=host|client        是否启用、以及这个窗口的角色
///   PWA_COOP_INDEX=0|1          客户端下标（决定 NetId；host 固定 0）
///   PWA_COOP_ENDPOINT=host:port broker 地址（默认 127.0.0.1:33772）
///   PWA_COOP_SESSION=...        会话 id（两个窗口必须一致，默认 pwa）
/// host 窗口会自己在本进程内跑 broker 中继；client 窗口只连它。
/// </summary>
public sealed record BrokerModeSettings(
    bool Enabled,
    Protocol.BrokerClientConfig? Config,
    string BindAddress,
    string ClientId,
    string EventLogPath,
    string? FailureReason)
{
    public const string RoleVariable = "PWA_COOP";
    public const string IndexVariable = "PWA_COOP_INDEX";
    public const string EndpointVariable = "PWA_COOP_ENDPOINT";
    public const string SessionVariable = "PWA_COOP_SESSION";
    public const string BindVariable = "PWA_COOP_BIND";
    public const string TokenVariable = "PWA_COOP_TOKEN";

    private const string DefaultEndpoint = "127.0.0.1:33772";
    private const string DefaultSession = "pwa";

    public static BrokerModeSettings LoadFromDirectory(string modDirectory)
    {
        return Load(modDirectory, Environment.GetEnvironmentVariable);
    }

    public static BrokerModeSettings Load(
        string modDirectory,
        Func<string, string?> getEnvironmentVariable)
    {
        var roleText = getEnvironmentVariable(RoleVariable)?.Trim().ToLowerInvariant();
        if (roleText is not ("host" or "client" or "1"))
        {
            return Disabled(modDirectory, null);
        }

        try
        {
            var role = roleText == "host" ? Protocol.BrokerClientRole.Host : Protocol.BrokerClientRole.Client;
            var index = role == Protocol.BrokerClientRole.Host
                ? 0
                : Math.Max(1, int.TryParse(getEnvironmentVariable(IndexVariable), out var parsed) ? parsed : 1);
            var (host, port) = ParseEndpoint(getEnvironmentVariable(EndpointVariable) ?? DefaultEndpoint);
            var session = string.IsNullOrWhiteSpace(getEnvironmentVariable(SessionVariable))
                ? DefaultSession
                : getEnvironmentVariable(SessionVariable)!.Trim();

            var token = getEnvironmentVariable(TokenVariable)?.Trim();
            var bind = getEnvironmentVariable(BindVariable)?.Trim();
            var config = new Protocol.BrokerClientConfig(role, index, host, port, session, string.IsNullOrWhiteSpace(token) ? null : token).Validate();
            return new BrokerModeSettings(
                BindAddress: string.IsNullOrWhiteSpace(bind) ? "127.0.0.1" : bind,
                Enabled: true,
                Config: config,
                ClientId: $"client-{index}",
                EventLogPath: EventLogPathFor(modDirectory, config),
                FailureReason: null);
        }
        catch (Exception exception)
        {
            return Disabled(modDirectory, exception.Message);
        }
    }

    private static (string Host, int Port) ParseEndpoint(string endpoint)
    {
        var parts = endpoint.Split(':');
        if (parts.Length != 2 || !int.TryParse(parts[1], out var port) || port <= 0 || port > 65535)
        {
            throw new FormatException($"endpoint 必须是 host:port，收到 {endpoint}");
        }

        return (parts[0], port);
    }

    private static BrokerModeSettings Disabled(string modDirectory, string? failureReason)
    {
        return new BrokerModeSettings(false, null, "127.0.0.1", "client-0", EventLogPathFor(modDirectory, null), failureReason);
    }

    private static string EventLogPathFor(string modDirectory, Protocol.BrokerClientConfig? config)
    {
        var label = config == null ? "disabled" : $"{config.Role}-{config.ClientIndex}";
        return Path.Combine(modDirectory, $"pwa-coop-{label}-events.txt");
    }
}
