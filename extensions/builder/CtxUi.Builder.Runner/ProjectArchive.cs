using System.Formats.Tar;
using System.IO.Compression;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace CtxUi.Builder.Runner;

public sealed class ProjectArchiveException(string message) : Exception(message);

public static class ProjectArchive
{
    public const long MaxUnpackedBytes = 32L * 1024 * 1024;

    private const string ConfigFile = "fastmcp.json";
    private const string EnvironmentKey = "environment";
    private const string DependenciesKey = "dependencies";
    private const int MaxConfigBytes = 1024 * 1024;

    public static string EnvironmentHash(byte[] archive)
    {
        var config = ReadConfig(archive);
        var canonical = Canonicalize(ParseEnvironment(config));
        return Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(canonical.ToJsonString())));
    }

    private static byte[] ReadConfig(byte[] archive)
    {
        byte[]? config = null;
        long total = 0;
        try
        {
            using var gzip = new GZipStream(new MemoryStream(archive), CompressionMode.Decompress);
            using var tar = new TarReader(gzip);
            while (tar.GetNextEntry() is { } entry)
            {
                RequireSafe(entry);
                total += entry.Length;
                if (total > MaxUnpackedBytes)
                {
                    throw new ProjectArchiveException($"The project archive unpacks beyond the limit of {MaxUnpackedBytes / (1024 * 1024)} MB.");
                }

                if (Normalize(entry.Name) == ConfigFile)
                    config = ReadConfigEntry(entry);
            }
        }
        catch (Exception failure) when (failure is InvalidDataException or FormatException or EndOfStreamException or ArgumentException)
        {
            throw new ProjectArchiveException($"The project archive is not a valid tar.gz: {failure.Message}");
        }

        return config ?? throw new ProjectArchiveException($"The project archive has no {ConfigFile}.");
    }

    private static byte[] ReadConfigEntry(TarEntry entry)
    {
        if (entry.Length > MaxConfigBytes || entry.DataStream is not { } data)
        {
            throw new ProjectArchiveException($"{ConfigFile} is empty or larger than {MaxConfigBytes / 1024} KB.");
        }

        using var buffer = new MemoryStream();
        data.CopyTo(buffer);
        return buffer.ToArray();
    }

    private static void RequireSafe(TarEntry entry)
    {
        if (entry.EntryType is not (TarEntryType.RegularFile or TarEntryType.V7RegularFile or TarEntryType.Directory))
        {
            throw new ProjectArchiveException($"The project archive contains '{entry.Name}', which is neither a file nor a directory.");
        }

        if (entry.Name.StartsWith('/') || entry.Name.Split('/', '\\').Contains(".."))
        {
            throw new ProjectArchiveException($"The project archive contains the path '{entry.Name}' outside the project.");
        }
    }

    private static string Normalize(string name) => name.StartsWith("./", StringComparison.Ordinal) ? name[2..] : name;

    private static JsonObject ParseEnvironment(byte[] config)
    {
        JsonNode? root;
        try
        {
            root = JsonNode.Parse(config);
        }
        catch (JsonException failure)
        {
            throw new ProjectArchiveException($"{ConfigFile} is not valid JSON: {failure.Message}");
        }

        try
        {
            return root is JsonObject document && document[EnvironmentKey] is JsonObject environment
                ? environment
                : throw new ProjectArchiveException($"{ConfigFile} has no {EnvironmentKey} section.");
        }
        catch (ArgumentException failure)
        {
            throw new ProjectArchiveException($"{ConfigFile} is not valid JSON: {failure.Message}");
        }
    }

    private static JsonObject Canonicalize(JsonObject environment)
    {
        JsonNode? dependencies = null;
        if (environment[DependenciesKey] is { } declared)
        {
            if (declared is not JsonArray list || list.Any(item => item?.GetValueKind() != JsonValueKind.String))
            {
                throw new ProjectArchiveException($"{ConfigFile}: {EnvironmentKey}.{DependenciesKey} must be a list of strings.");
            }

            dependencies = new JsonArray(list
                .Select(item => item!.GetValue<string>())
                .Distinct(StringComparer.Ordinal)
                .Order(StringComparer.Ordinal)
                .Select(item => (JsonNode?)JsonValue.Create(item))
                .ToArray());
        }

        var canonical = (JsonObject)Sorted(environment)!;
        if (dependencies is not null)
            canonical[DependenciesKey] = dependencies;

        return canonical;
    }

    private static JsonNode? Sorted(JsonNode? node) => node switch
    {
        JsonObject value => new JsonObject(value
            .OrderBy(property => property.Key, StringComparer.Ordinal)
            .Select(property => KeyValuePair.Create(property.Key, Sorted(property.Value)))),
        JsonArray value => new JsonArray(value.Select(Sorted).ToArray()),
        _ => node?.DeepClone()
    };
}
