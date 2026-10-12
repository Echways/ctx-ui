using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Http.Features;
using Microsoft.Extensions.DependencyInjection;

namespace CtxUi.Gateway.Tests;

internal static class Pipeline
{
    /// <summary>Прогоняет запрос через UseTokenPath без сервера и возвращает контекст после обработки.</summary>
    public static async Task<HttpContext> RunAsync(string path, string query = "", string? authorization = null)
    {
        var app = new ApplicationBuilder(new ServiceCollection().BuildServiceProvider());
        app.UseTokenPath();
        app.Run(_ => Task.CompletedTask);

        var context = new DefaultHttpContext();
        context.Request.Path = path;
        context.Request.QueryString = new QueryString(query);
        context.Features.GetRequiredFeature<IHttpRequestFeature>().RawTarget = path + query;
        if (authorization is not null)
            context.Request.Headers.Authorization = authorization;

        await app.Build()(context);
        return context;
    }
}
