using PwA.Coop.Protocol;

namespace PwA.Coop.Broker;

public sealed class InMemoryBrokerSession
{
    private readonly Dictionary<string, BrokerClientRegistration> _clientsById = new(StringComparer.Ordinal);
    private readonly Dictionary<int, string> _clientIdsByIndex = new();

    public InMemoryBrokerSession(string sessionId)
    {
        if (string.IsNullOrWhiteSpace(sessionId))
        {
            throw new ArgumentException("Session id must not be blank.", nameof(sessionId));
        }

        SessionId = sessionId;
    }

    public string SessionId { get; }

    public IReadOnlyList<BrokerClientRegistration> Clients =>
        _clientsById.Values
            .OrderBy(client => client.ClientIndex)
            .ToArray();

    public string? HostClientId { get; private set; }

    public BrokerClientRegistration? FindClient(string clientId)
    {
        return _clientsById.TryGetValue(clientId, out var registration)
            ? registration
            : null;
    }

    public void Register(BrokerClientRegistration registration)
    {
        if (string.IsNullOrWhiteSpace(registration.ClientId))
        {
            throw new InvalidOperationException("client id must not be blank.");
        }

        if (registration.ClientIndex is < 0 or > 3)
        {
            throw new InvalidOperationException("client index must be 0 through 3.");
        }

        if (_clientsById.ContainsKey(registration.ClientId))
        {
            throw new InvalidOperationException($"client '{registration.ClientId}' is already registered.");
        }

        if (_clientIdsByIndex.TryGetValue(registration.ClientIndex, out var existingClientId))
        {
            throw new InvalidOperationException(
                $"client index {registration.ClientIndex} is already registered by '{existingClientId}'.");
        }

        if (registration.Role == BrokerClientRole.Host)
        {
            if (HostClientId is not null)
            {
                throw new InvalidOperationException($"host is already registered by '{HostClientId}'.");
            }

            HostClientId = registration.ClientId;
        }

        _clientsById.Add(registration.ClientId, registration);
        _clientIdsByIndex.Add(registration.ClientIndex, registration.ClientId);
    }

    public void Unregister(string clientId)
    {
        if (!_clientsById.Remove(clientId, out var registration))
        {
            return;
        }

        _clientIdsByIndex.Remove(registration.ClientIndex);
        if (string.Equals(HostClientId, clientId, StringComparison.Ordinal))
        {
            HostClientId = null;
        }
    }

    public IReadOnlyList<BrokerRoute> Route(BrokerEnvelope envelope)
    {
        if (!string.Equals(envelope.SessionId, SessionId, StringComparison.Ordinal))
        {
            throw new InvalidOperationException($"envelope session '{envelope.SessionId}' does not match broker session '{SessionId}'.");
        }

        if (!_clientsById.TryGetValue(envelope.SourceClientId, out var source))
        {
            throw new InvalidOperationException($"source client '{envelope.SourceClientId}' is not registered.");
        }

        if (envelope.TargetClientId is { } targetClientId)
        {
            return [new BrokerRoute(targetClientId, envelope)];
        }

        if (source.Role == BrokerClientRole.Client)
        {
            if (HostClientId is null)
            {
                throw new InvalidOperationException("cannot route untargeted client envelope because no host is registered.");
            }

            return [new BrokerRoute(HostClientId, envelope)];
        }

        return _clientsById.Keys
            .Where(clientId => !string.Equals(clientId, envelope.SourceClientId, StringComparison.Ordinal))
            .OrderBy(clientId => _clientsById[clientId].ClientIndex)
            .Select(clientId => new BrokerRoute(clientId, envelope))
            .ToArray();
    }
}
