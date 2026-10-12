using CtxUi.Core.Inbounds;

namespace CtxUi.Core.Clients;

public sealed class ClientInbound
{
    public Guid ClientId { get; set; }
    public Guid InboundId { get; set; }
    public Inbound? Inbound { get; set; }
}
