using System.Text.Encodings.Web;
using System.Text.Json;
using Microsoft.AspNetCore.Http;
using CtxUi.Core.Routing;

namespace CtxUi.Gateway;

internal static class Rejection
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web)
    {
        Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
    };

    public static Task WriteAsync(HttpContext context, AccessDecision decision)
    {
        var (status, error, message) = decision.Status switch
        {
            AccessStatus.TokenMissing => (
                StatusCodes.Status401Unauthorized,
                "token_missing",
                "нужен токен: заголовок Authorization: Bearer <токен> или адрес /k/<токен>/mcp/<inbound>"),
            AccessStatus.TokenUnknown => (StatusCodes.Status401Unauthorized, "token_unknown", "токен не найден"),
            AccessStatus.ClientDisabled => (StatusCodes.Status401Unauthorized, "client_disabled", "доступ отключён"),
            AccessStatus.ClientExpired => (
                StatusCodes.Status401Unauthorized,
                "client_expired",
                $"срок действия истёк {decision.Client!.ExpiresAt:yyyy-MM-dd}"),
            AccessStatus.InboundNotFound => (
                StatusCodes.Status404NotFound,
                "inbound_not_found",
                "inbound не найден или недоступен этому клиенту"),
            _ => throw new InvalidOperationException($"для исхода {decision.Status} отказ не определён"),
        };

        context.Response.StatusCode = status;
        if (status == StatusCodes.Status401Unauthorized)
            context.Response.Headers.WWWAuthenticate = "Bearer";

        return context.Response.WriteAsJsonAsync(new Body(error, message), Json);
    }

    private sealed record Body(string Error, string Message);
}
