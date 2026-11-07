using CtxUi.Core.SignIn;

namespace CtxUi.Core.Clients;

public sealed record ClientAccessPolicy(int? MaxDevices, bool DirectKey)
{
    public static ClientAccessPolicy Of(Client client)
    {
        return new ClientAccessPolicy(client.MaxDevices, client.DirectKey);
    }

    public ClientAccessPolicy Validated()
    {
        DeviceLimit.Validated(MaxDevices);
        return this;
    }
}
