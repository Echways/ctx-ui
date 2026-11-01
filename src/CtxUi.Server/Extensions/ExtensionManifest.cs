using System.Text.Json;

namespace CtxUi.Server.Extensions;

public sealed record ExtensionManifest(string Id, string Name, string Version, string Api, string Assembly)
{
    public const string FileName = "extension.json";

    private static readonly JsonSerializerOptions Json = new() { PropertyNameCaseInsensitive = true };

    public static ExtensionManifest Read(string directory)
    {
        var manifest = Parse(Path.Combine(directory, FileName));
        string?[] fields = [manifest.Id, manifest.Name, manifest.Version, manifest.Api, manifest.Assembly];
        if (fields.Any(string.IsNullOrWhiteSpace))
            throw new InvalidDataException(ServerText.ExtensionManifestIncomplete);
        if (Path.GetFileName(manifest.Assembly) != manifest.Assembly)
            throw new InvalidDataException(string.Format(ServerText.ExtensionManifestAssemblyOutside, manifest.Assembly));
        return manifest;
    }

    private static ExtensionManifest Parse(string path)
    {
        try
        {
            return JsonSerializer.Deserialize<ExtensionManifest>(File.ReadAllText(path), Json)
                ?? throw new InvalidDataException(ServerText.ExtensionManifestBroken);
        }
        catch (Exception failure) when (failure is IOException or JsonException)
        {
            throw new InvalidDataException(ServerText.ExtensionManifestBroken, failure);
        }
    }
}
