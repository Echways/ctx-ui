using Microsoft.AspNetCore.Routing;

namespace CtxUi.Extensions;

public interface IPanelExtension
{
    void Register(ExtensionContext context);

    void MapEndpoints(IEndpointRouteBuilder endpoints)
    {
    }
}
