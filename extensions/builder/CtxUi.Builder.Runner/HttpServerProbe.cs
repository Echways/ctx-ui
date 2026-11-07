namespace CtxUi.Builder.Runner;

public interface IServerProbe
{
    string Address(string container);
    Task<bool> IsHealthyAsync(string container, CancellationToken ct);
}

public sealed class HttpServerProbe : IServerProbe, IDisposable
{
    private const int ServerPort = 8000;

    private readonly HttpClient _client = new(new SocketsHttpHandler { UseProxy = false })
    {
        Timeout = TimeSpan.FromSeconds(3)
    };

    public string Address(string container)
    {
        return $"http://{container}:{ServerPort}";
    }

    public async Task<bool> IsHealthyAsync(string container, CancellationToken ct)
    {
        try
        {
            using var response = await _client.GetAsync($"{Address(container)}/healthz", ct);
            return response.IsSuccessStatusCode;
        }
        catch (HttpRequestException)
        {
            return false;
        }
        catch (TaskCanceledException) when (!ct.IsCancellationRequested)
        {
            return false;
        }
    }

    public void Dispose()
    {
        _client.Dispose();
    }
}
