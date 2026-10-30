using PwA.Coop.Protocol;

namespace PwA.Coop.Broker;

public sealed record BrokerClientRegistration(
    string ClientId,
    BrokerClientRole Role,
    int ClientIndex)
{
    public BrokerClientRegistrationDto ToDto()
    {
        return new BrokerClientRegistrationDto(ClientId, Role, ClientIndex);
    }

    public static BrokerClientRegistration FromDto(BrokerClientRegistrationDto dto)
    {
        return new BrokerClientRegistration(dto.ClientId, dto.Role, dto.ClientIndex);
    }
}

