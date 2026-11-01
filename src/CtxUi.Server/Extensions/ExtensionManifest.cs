using System.Text.Json;

namespace CtxUi.Server.Extensions;

public sealed record ExtensionManifest(string Id, string Name, string Version, string Api, string Assembly)
{
    public const string FileName = "extension.json";

    private static readonly JsonSerializerOptions Json = new() { PropertyNameCaseInsensitive = true };

    public static ExtensionManifest Read(string directory)
    {
        var manifest = JsonSerializer.Deserialize<ExtensionManifest>(File.ReadAllText(Path.Combine(directory, FileName)), Json)
            ?? throw new InvalidDataException($"{FileName} is empty");
        string?[] fields = [manifest.Id, manifest.Name, manifest.Version, manifest.Api, manifest.Assembly];
        if (fields.Any(string.IsNullOrWhiteSpace))
            throw new InvalidDataException($"{FileName} must set id, name, version, api and assembly");
        if (Path.GetFileName(manifest.Assembly) != manifest.Assembly)
            throw new InvalidDataException($"{FileName} names an assembly outside the folder: {manifest.Assembly}");
        return manifest;
    }
}
