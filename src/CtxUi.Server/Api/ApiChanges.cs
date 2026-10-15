using CtxUi.Core;
using CtxUi.Core.Routing;
using CtxUi.Panel;

namespace CtxUi.Server.Api;

public sealed class ApiChanges(IRoutingRefresh routing) : IEndpointFilter
{
    public async ValueTask<object?> InvokeAsync(EndpointFilterInvocationContext context, EndpointFilterDelegate next)
    {
        try
        {
            var result = await next(context);
            if (!HttpMethods.IsGet(context.HttpContext.Request.Method))
                await routing.RefreshAsync(context.HttpContext.RequestAborted);

            return result;
        }
        catch (NotFoundException exception)
        {
            return TypedResults.Problem(statusCode: StatusCodes.Status404NotFound, detail: Words.Sentence(exception.Message));
        }
        catch (DomainException exception)
        {
            return TypedResults.Problem(statusCode: StatusCodes.Status400BadRequest, detail: Words.Sentence(exception.Message));
        }
    }
}
