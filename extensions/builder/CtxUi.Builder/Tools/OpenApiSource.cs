using System.Text;

namespace CtxUi.Builder.Tools;

public static class OpenApiSource
{
    public static bool IsGiven(string? address, string? pasted)
    {
        return !string.IsNullOrWhiteSpace(address) || !string.IsNullOrWhiteSpace(pasted);
    }

    public static async Task<ImportedApi> ReadAsync(string? address, string? pasted, IHttpClientFactory clients,
        CancellationToken ct)
    {
        if (!string.IsNullOrWhiteSpace(pasted))
        {
            using var stream = new MemoryStream(Encoding.UTF8.GetBytes(pasted));
            return await OpenApiImport.ParseAsync(stream, ct);
        }

        if (!Uri.TryCreate(address?.Trim(), UriKind.Absolute, out var uri))
            throw new BuilderException(BuilderText.ImportAddressInvalid);
        return await OpenApiImport.FetchAsync(uri, clients.CreateClient(), ct);
    }
}
