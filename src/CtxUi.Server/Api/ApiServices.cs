using System.Text.Json.Serialization;
using Microsoft.AspNetCore.Authentication;
using Microsoft.OpenApi;

namespace CtxUi.Server.Api;

public static class ApiServices
{
    public const string Document = "v1";
    public const string Prefix = "/api/v1";
    public const string SwaggerPath = "swagger";

    private const string Policy = "api";

    public static IServiceCollection AddCtxUiApi(this IServiceCollection services)
    {
        services.AddAuthentication().AddScheme<AuthenticationSchemeOptions, ApiKeyAuthentication>(ApiKeyAuthentication.SchemeName, null);
        services.AddAuthorizationBuilder()
            .AddPolicy(Policy, policy => policy.AddAuthenticationSchemes(ApiKeyAuthentication.SchemeName).RequireAuthenticatedUser());
        services.AddProblemDetails();
        services.ConfigureHttpJsonOptions(options => options.SerializerOptions.Converters.Add(new JsonStringEnumConverter()));
        services.AddOpenApi(Document, options =>
        {
            options.ShouldInclude = description => description.RelativePath?.StartsWith("api/", StringComparison.Ordinal) == true;
            options.AddDocumentTransformer((document, _, _) =>
            {
                document.Info = new OpenApiInfo
                {
                    Title = "ctx-ui",
                    Version = Document,
                    Description = "Inbound'ы, клиенты и учёт вызовов. Ключ выпускается командой ctx-ui apikey add <название> " +
                        "или в панели и передаётся заголовком Authorization: Bearer <ключ>.",
                };
                document.Components ??= new OpenApiComponents();
                document.Components.SecuritySchemes ??= new Dictionary<string, IOpenApiSecurityScheme>();
                document.Components.SecuritySchemes[ApiKeyAuthentication.SchemeName] = new OpenApiSecurityScheme
                {
                    Type = SecuritySchemeType.Http,
                    Scheme = "bearer",
                    Description = "ключ API вида ctxk_…",
                };
                document.Security = [new OpenApiSecurityRequirement { [new OpenApiSecuritySchemeReference(ApiKeyAuthentication.SchemeName, document)] = [] }];
                return Task.CompletedTask;
            });
        });
        return services;
    }

    public static IApplicationBuilder UseCtxUiApiDocs(this IApplicationBuilder app)
    {
        app.UseWhen(
            context => context.Request.Path.StartsWithSegments("/api"),
            api => api.UseStatusCodePages());
        return app.UseSwaggerUI(options =>
        {
            options.RoutePrefix = SwaggerPath;
            options.SwaggerEndpoint($"../openapi/{Document}.json", $"ctx-ui {Document}");
            options.DocumentTitle = "ctx-ui API";
        });
    }

    public static WebApplication MapCtxUiApi(this WebApplication app)
    {
        app.MapOpenApi("/openapi/{documentName}.json");
        var api = app.MapGroup(Prefix).RequireAuthorization(Policy).AddEndpointFilter<ApiChanges>();
        api.MapInbounds();
        api.MapClients();
        api.MapUsage();
        app.Map("/api/{**rest}", () => Results.NotFound()).ExcludeFromDescription();
        return app;
    }
}
