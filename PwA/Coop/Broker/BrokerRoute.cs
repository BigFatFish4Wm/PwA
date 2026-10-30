using PwA.Coop.Protocol;

namespace PwA.Coop.Broker;

public sealed record BrokerRoute(string TargetClientId, BrokerEnvelope Envelope);

