using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Routing;
using static OpenIddict.Abstractions.OpenIddictConstants;

namespace CtxUi.Connect.Registration;

public sealed record RegistrationResponse(
    [property: JsonPropertyName("client_id")] string ClientId,
    [property: JsonPropertyName("client_name")] string ClientName,
    [property: JsonPropertyName("redirect_uris")] IReadOnlyList<string> RedirectUris,
    [property: JsonPropertyName("grant_types")] IReadOnlyList<string> GrantTypes,
    [property: JsonPropertyName("response_types")] IReadOnlyList<string> ResponseTypes,
    [property: JsonPropertyName("token_endpoint_auth_method")] string TokenEndpointAuthMethod,
    [property: JsonPropertyName("client_id_issued_at")] long ClientIdIssuedAt);

public sealed record RegistrationError(
    [property: JsonPropertyName("error")] string Error,
    [property: JsonPropertyName("error_description")] string? ErrorDescription);

public static class RegistrationEndpoint
{
    public const string Path = ConnectServices.Prefix + "/register";

    public const long MaxBodyBytes = 16 * 1024;

    public static IEndpointRouteBuilder MapRegistration(this IEndpointRouteBuilder app)
    {
        app.MapPost(Path, RegisterAsync).DisableAntiforgery().WithMetadata(new RequestSizeLimitAttribute(MaxBodyBytes));
        return app;
    }

    private static async Task<IResult> RegisterAsync(HttpRequest request, RegisteredApps apps, TimeProvider clock, CancellationToken ct)
    {
        JsonElement body;
        try
        {
            body = await JsonSerializer.DeserializeAsync<JsonElement>(request.Body, cancellationToken: ct);
        }
        catch (JsonException)
        {
            return Results.BadRequest(new RegistrationError("invalid_client_metadata", "the body must be a JSON object"));
        }
        catch (BadHttpRequestException exception) when (exception.StatusCode == StatusCodes.Status413PayloadTooLarge)
        {
            return Results.Json(
                new RegistrationError("invalid_client_metadata", $"the body must not exceed {MaxBodyBytes} bytes"),
                statusCode: StatusCodes.Status413PayloadTooLarge);
        }

        var result = await apps.RegisterAsync(body, ct);
        if (result.ClientId is null)
            return Results.BadRequest(new RegistrationError(result.Error!, result.ErrorDescription));

        return Results.Json(
            new RegistrationResponse(
                result.ClientId,
                result.ClientName,
                result.RedirectUris,
                [GrantTypes.AuthorizationCode, GrantTypes.RefreshToken],
                [ResponseTypes.Code],
                ClientAuthenticationMethods.None,
                clock.GetUtcNow().ToUnixTimeSeconds()),
            statusCode: StatusCodes.Status201Created);
    }
}
