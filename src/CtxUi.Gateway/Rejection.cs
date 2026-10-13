using System.Buffers;
using System.Globalization;
using System.Text.Encodings.Web;
using System.Text.Json;
using Microsoft.AspNetCore.Http;
using CtxUi.Core.Routing;

namespace CtxUi.Gateway;

internal static class Rejection
{
    private const int InvalidRequest = -32600;
    private const int ServerError = -32000;

    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web)
    {
        Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
    };

    private static readonly JsonWriterOptions Writer = new() { Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping };

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

    public static Task WriteRateLimitedAsync(HttpContext context, int retryAfterSeconds) => WriteLimitAsync(
        context,
        retryAfterSeconds,
        "rate_limited",
        $"слишком много запросов в минуту, повторите через {retryAfterSeconds} с");

    public static Task WriteBusyAsync(HttpContext context) => WriteLimitAsync(
        context,
        1,
        "too_many_concurrent",
        "слишком много одновременных запросов, дождитесь ответа на предыдущие");

    public static Task WriteBodyTooLargeAsync(HttpContext context, int limitBytes)
    {
        context.Response.StatusCode = StatusCodes.Status413PayloadTooLarge;
        return context.Response.WriteAsJsonAsync(
            new Body("body_too_large", $"тело запроса больше допустимого: {limitBytes} байт"),
            Json);
    }

    public static Task WriteNotJsonRpcAsync(HttpContext context)
    {
        var buffer = new ArrayBufferWriter<byte>();
        using (var json = new Utf8JsonWriter(buffer, Writer))
        {
            json.WriteStartObject();
            json.WriteString("jsonrpc", "2.0");
            json.WriteNull("id");
            WriteError(
                json,
                InvalidRequest,
                $"шлюз не разобрал тело запроса: нужно одно сообщение JSON-RPC или пакет до {RpcEnvelope.MaxBatch} сообщений, без повторяющихся полей");
            json.WriteEndObject();
        }

        return WriteJsonAsync(context, StatusCodes.Status400BadRequest, buffer);
    }

    public static Task WriteQuotaExhaustedAsync(HttpContext context, RpcEnvelope envelope, string text)
    {
        var answered = envelope.Calls.Where(call => call.Id is not null).ToList();
        if (answered.Count == 0)
        {
            context.Response.StatusCode = StatusCodes.Status202Accepted;
            return Task.CompletedTask;
        }

        var buffer = new ArrayBufferWriter<byte>();
        using (var json = new Utf8JsonWriter(buffer, Writer))
        {
            if (envelope.IsBatch)
                json.WriteStartArray();

            foreach (var call in answered)
            {
                json.WriteStartObject();
                json.WriteString("jsonrpc", "2.0");
                json.WritePropertyName("id");
                json.WriteRawValue(call.Id!);
                if (call.IsToolCall)
                    WriteToolError(json, text);
                else
                    WriteError(json, ServerError, "Запрос не выполнен: он пришёл в одном пакете с вызовами сверх лимита. " + text);

                json.WriteEndObject();
            }

            if (envelope.IsBatch)
                json.WriteEndArray();
        }

        return WriteJsonAsync(context, StatusCodes.Status200OK, buffer);
    }

    private static void WriteToolError(Utf8JsonWriter json, string text)
    {
        json.WriteStartObject("result");
        json.WriteStartArray("content");
        json.WriteStartObject();
        json.WriteString("type", "text");
        json.WriteString("text", text);
        json.WriteEndObject();
        json.WriteEndArray();
        json.WriteBoolean("isError", true);
        json.WriteEndObject();
    }

    private static void WriteError(Utf8JsonWriter json, int code, string message)
    {
        json.WriteStartObject("error");
        json.WriteNumber("code", code);
        json.WriteString("message", message);
        json.WriteEndObject();
    }

    private static async Task WriteJsonAsync(HttpContext context, int status, ArrayBufferWriter<byte> body)
    {
        context.Response.StatusCode = status;
        context.Response.ContentType = "application/json";
        context.Response.ContentLength = body.WrittenCount;
        await context.Response.Body.WriteAsync(body.WrittenMemory, context.RequestAborted);
    }

    private static Task WriteLimitAsync(HttpContext context, int retryAfterSeconds, string error, string message)
    {
        context.Response.StatusCode = StatusCodes.Status429TooManyRequests;
        context.Response.Headers.RetryAfter = retryAfterSeconds.ToString(CultureInfo.InvariantCulture);
        return context.Response.WriteAsJsonAsync(new Body(error, message), Json);
    }

    private sealed record Body(string Error, string Message);
}
