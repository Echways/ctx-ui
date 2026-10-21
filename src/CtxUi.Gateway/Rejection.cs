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
            AccessStatus.TokenMissing => (StatusCodes.Status401Unauthorized, "token_missing", GatewayText.TokenMissing),
            AccessStatus.TokenUnknown => (StatusCodes.Status401Unauthorized, "token_unknown", GatewayText.TokenUnknown),
            AccessStatus.ClientDisabled => (StatusCodes.Status401Unauthorized, "client_disabled", GatewayText.ClientDisabled),
            AccessStatus.ClientExpired => (
                StatusCodes.Status401Unauthorized,
                "client_expired",
                string.Format(
                    GatewayText.ClientExpired,
                    decision.Client!.ExpiresAt!.Value.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture))),
            AccessStatus.InboundNotFound => (StatusCodes.Status404NotFound, "inbound_not_found", GatewayText.InboundNotFound),
            _ => throw new InvalidOperationException($"no rejection is defined for {decision.Status}"),
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
        string.Format(GatewayText.RateLimited, retryAfterSeconds));

    public static Task WriteBusyAsync(HttpContext context) => WriteLimitAsync(
        context,
        1,
        "too_many_concurrent",
        GatewayText.TooManyConcurrent);

    public static Task WriteBodyTooLargeAsync(HttpContext context, int limitBytes)
    {
        context.Response.StatusCode = StatusCodes.Status413PayloadTooLarge;
        return context.Response.WriteAsJsonAsync(
            new Body("body_too_large", string.Format(GatewayText.BodyTooLarge, limitBytes)),
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
            WriteError(json, InvalidRequest, string.Format(GatewayText.NotJsonRpc, RpcEnvelope.MaxBatch));
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
                    WriteError(json, ServerError, string.Format(GatewayText.BatchRefused, text));

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
