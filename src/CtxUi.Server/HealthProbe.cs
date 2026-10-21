using System.Net;

namespace CtxUi.Server;

public static class HealthProbe
{
    public const string Path = "/healthz";

    private const string Http = "http://";

    private const string DefaultListener = "http://127.0.0.1:8080";

    private static readonly string[] AnyAddress = ["0.0.0.0", "*", "+", "[::]"];

    private static readonly TimeSpan Patience = TimeSpan.FromSeconds(3);

    public static string Target(string? urls)
    {
        var listener = urls?
            .Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .FirstOrDefault(url => url.StartsWith(Http, StringComparison.OrdinalIgnoreCase)) ?? DefaultListener;
        var authority = listener[Http.Length..].TrimEnd('/');
        var portAt = authority.LastIndexOf(':');
        var (host, port) = portAt > authority.LastIndexOf(']') ? (authority[..portAt], authority[portAt..]) : (authority, "");
        if (AnyAddress.Contains(host))
            host = "127.0.0.1";

        return Http + host + port + Path;
    }

    public static IConfiguration Configuration() => new ConfigurationBuilder()
        .AddEnvironmentVariables("ASPNETCORE_")
        .AddJsonFile(System.IO.Path.Combine(AppContext.BaseDirectory, "appsettings.json"), optional: true)
        .AddEnvironmentVariables()
        .Build();

    public static async Task<int> RunAsync(IConfiguration configuration, TextWriter error)
    {
        var target = Target(configuration["Urls"]);
        using var http = new HttpClient { Timeout = Patience };
        try
        {
            using var response = await http.GetAsync(target);
            if (response.StatusCode == HttpStatusCode.OK)
                return 0;

            error.WriteLine($"{target}: {(int)response.StatusCode} {response.ReasonPhrase}");
        }
        catch (Exception exception) when (exception is HttpRequestException or TaskCanceledException)
        {
            error.WriteLine($"{target}: {exception.Message}");
        }

        return 1;
    }
}
