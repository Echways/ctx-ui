using System.Formats.Tar;
using System.IO.Compression;
using System.Reflection;
using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.RegularExpressions;
using CtxUi.Builder.Data;

namespace CtxUi.Builder.Servers;

public static partial class ProjectFiles
{
    public const long MaxProjectBytes = 16 * 1024 * 1024;
    public const int MaxArchiveEntries = 2000;
    public const string FastMcpVersion = "4.1.4";

    private const string ProjectRoot = "mcp/";
    private const string OpenApiPath = "openapi.json";
    private const string BuilderPath = "builder.json";
    private const string FastMcpPath = "fastmcp.json";
    private const string MainPath = "main.py";
    private const string PythonVersion = "3.13";
    private const int MaxPathLength = 255;
    private const int MaxSegmentLength = 100;
    private const int MaxDependencyLength = 256;
    private const int TarEntryOverheadBytes = 2048;
    private const int CopyBufferBytes = 81920;
    private const string MainResource = "runtime.main.py";

    private static readonly UTF8Encoding StrictUtf8 = new(false, true);

    private static readonly HashSet<string> DataExtensions =
        new(StringComparer.OrdinalIgnoreCase) { ".py", ".json", ".txt", ".md", ".csv", ".tsv", ".yaml", ".yml", ".toml", ".xml", ".html", ".sql" };

    private static readonly HashSet<string> PythonKeywords =
    [
        "False", "None", "True", "and", "as", "assert", "async", "await", "break", "class", "continue", "def", "del", "elif", "else",
        "except", "finally", "for", "from", "global", "if", "import", "in", "is", "lambda", "nonlocal", "not", "or", "pass", "raise",
        "return", "try", "while", "with", "yield",
    ];

    [GeneratedRegex(@"^fastmcp\s*($|[\[=<>!~;@\s])", RegexOptions.IgnoreCase)]
    private static partial Regex FastMcpRequirement();

    [GeneratedRegex("[^a-z0-9_]+")]
    private static partial Regex NotIdentifier();

    public static string ValidatePath(string path)
    {
        if (string.IsNullOrEmpty(path) || path.Length > MaxPathLength || path.Any(char.IsControl) || path.Contains('\\'))
            throw Unsafe(path);
        if (path is OpenApiPath or BuilderPath)
            return path;
        if (!path.StartsWith(ProjectRoot, StringComparison.Ordinal))
            throw Unsafe(path);

        var segments = path[ProjectRoot.Length..].Split('/');
        if (segments.Any(segment => segment.Length is 0 or > MaxSegmentLength || segment is "." or ".."))
            throw Unsafe(path);
        if (!DataExtensions.Contains(Path.GetExtension(segments[^1])))
            throw new ProjectFileException(string.Format(BuilderText.ProjectFileTypeNotAllowed, path));
        return path;
    }

    public static string NewToolTemplate(string name)
    {
        var function = ToolFunctionName(name);
        return $"from fastmcp.tools import tool\n\n\n@tool\ndef {function}(name: str) -> str:\n    \"\"\"Greets the caller by name.\"\"\"\n    return f\"Hello, {{name}}!\"\n";
    }

    public static string ToolFunctionName(string name)
    {
        var identifier = NotIdentifier().Replace(name.Trim().ToLowerInvariant(), "_").Trim('_');
        if (identifier.Length == 0)
            identifier = "tool";
        if (char.IsAsciiDigit(identifier[0]))
            identifier = "tool_" + identifier;
        if (PythonKeywords.Contains(identifier))
            identifier += "_";
        return identifier;
    }

    public static IReadOnlyList<string> ParseDependencies(string? text)
    {
        var result = new List<string>();
        foreach (var line in (text ?? string.Empty).Split('\n').Select(line => line.Trim()).Where(line => line.Length > 0))
        {
            if (line.StartsWith('-') || line.Length > MaxDependencyLength || line.Any(char.IsControl))
                throw new ProjectFileException(string.Format(BuilderText.DependencyInvalid, line));
            if (!FastMcpRequirement().IsMatch(line) && !result.Contains(line))
                result.Add(line);
        }

        return result;
    }

    public static async Task<byte[]> SnapshotAsync(ServerDraft draft, IReadOnlyList<DraftFile> files, string fastMcpVersion, CancellationToken ct)
    {
        var entries = new SortedDictionary<string, byte[]>(StringComparer.Ordinal)
        {
            [FastMcpPath] = Encoding.UTF8.GetBytes(FastMcpJson(draft.Dependencies, fastMcpVersion)),
            [MainPath] = await ReadMainAsync(ct),
        };
        foreach (var file in files.Where(file => file.Path.StartsWith(ProjectRoot, StringComparison.Ordinal)))
            entries[ValidatePath(file.Path)] = Encoding.UTF8.GetBytes(file.Content);
        if (draft.OpenApiJson is not null)
            entries[OpenApiPath] = Encoding.UTF8.GetBytes(draft.OpenApiJson);
        if (draft.BuilderJson is not null)
            entries[BuilderPath] = Encoding.UTF8.GetBytes(draft.BuilderJson);

        var output = new MemoryStream();
        await using (var gzip = new GZipStream(output, CompressionLevel.Optimal, leaveOpen: true))
        await using (var writer = new TarWriter(gzip, TarEntryFormat.Ustar, leaveOpen: true))
        {
            foreach (var (path, content) in entries)
            {
                var entry = new UstarTarEntry(TarEntryType.RegularFile, path)
                {
                    DataStream = new MemoryStream(content),
                    ModificationTime = DateTimeOffset.UnixEpoch,
                    Mode = UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.GroupRead | UnixFileMode.OtherRead,
                };
                await writer.WriteEntryAsync(entry, ct);
            }
        }

        return output.ToArray();
    }

    public static async Task<IReadOnlyList<(string Path, string Content)>> ReadArchiveAsync(Stream archive, CancellationToken ct) =>
        (await ImportArchiveAsync(archive, ct)).Files;

    public static async Task<ProjectImport> ImportArchiveAsync(Stream archive, CancellationToken ct)
    {
        var buffer = await BufferAsync(archive, ct);
        var collector = new ArchiveCollector();
        try
        {
            if (IsGzip(buffer))
                await ReadTarAsync(buffer, collector, ct);
            else if (IsZip(buffer))
                await ReadZipAsync(buffer, collector, ct);
            else
                throw new ProjectFileException(BuilderText.ArchiveUnreadable);
        }
        catch (Exception failure) when (failure is InvalidDataException or EndOfStreamException or IOException or ArgumentException or NotSupportedException)
        {
            throw new ProjectFileException(BuilderText.ArchiveUnreadable);
        }

        return new ProjectImport([.. collector.Files.Select(file => (file.Key, file.Value))], DependenciesOf(collector.FastMcpJson));
    }

    private static async Task ReadTarAsync(MemoryStream buffer, ArchiveCollector collector, CancellationToken ct)
    {
        await using var gzip = new GZipStream(buffer, CompressionMode.Decompress);
        await using var limited = new LimitedStream(gzip, MaxProjectBytes + (long)MaxArchiveEntries * TarEntryOverheadBytes);
        using var reader = new TarReader(limited);
        while (await reader.GetNextEntryAsync(copyData: false, ct) is { } entry)
        {
            switch (entry.EntryType)
            {
                case TarEntryType.GlobalExtendedAttributes:
                    continue;
                case TarEntryType.SymbolicLink or TarEntryType.HardLink:
                    throw new ProjectFileException(string.Format(BuilderText.ArchiveLinkRefused, entry.Name));
                case TarEntryType.Directory:
                    collector.Admit(entry.Name, true);
                    break;
                case TarEntryType.RegularFile or TarEntryType.V7RegularFile:
                    if (collector.Admit(entry.Name, false) is { } name && collector.Wants(name))
                        await collector.AddAsync(name, entry.DataStream, ct);
                    break;
                default:
                    throw new ProjectFileException(string.Format(BuilderText.ArchiveEntryKindRefused, entry.Name));
            }
        }
    }

    private static async Task ReadZipAsync(MemoryStream buffer, ArchiveCollector collector, CancellationToken ct)
    {
        using var zip = new ZipArchive(buffer, ZipArchiveMode.Read);
        if (zip.Entries.Count > MaxArchiveEntries)
            throw new ProjectFileException(string.Format(BuilderText.ArchiveTooManyEntries, MaxArchiveEntries));
        foreach (var entry in zip.Entries)
        {
            var isDirectory = entry.FullName.EndsWith('/');
            var kind = (entry.ExternalAttributes >> 16) & 0xF000;
            if (kind == 0xA000)
                throw new ProjectFileException(string.Format(BuilderText.ArchiveLinkRefused, entry.FullName));
            if (kind is not (0 or 0x8000 or 0x4000))
                throw new ProjectFileException(string.Format(BuilderText.ArchiveEntryKindRefused, entry.FullName));

            if (collector.Admit(entry.FullName, isDirectory) is { } name && collector.Wants(name))
            {
                await using var stream = entry.Open();
                await collector.AddAsync(name, stream, ct);
            }
        }
    }

    private static async Task<MemoryStream> BufferAsync(Stream archive, CancellationToken ct)
    {
        var buffer = new MemoryStream();
        var chunk = new byte[CopyBufferBytes];
        int read;
        while ((read = await archive.ReadAsync(chunk, ct)) > 0)
        {
            if (buffer.Length + read > MaxProjectBytes * 2)
                throw new ProjectFileException(BuilderText.ArchiveTooLarge);
            buffer.Write(chunk, 0, read);
        }

        buffer.Position = 0;
        return buffer;
    }

    private static bool IsGzip(MemoryStream buffer) => buffer.Length > 2 && buffer.GetBuffer()[0] == 0x1f && buffer.GetBuffer()[1] == 0x8b;

    private static bool IsZip(MemoryStream buffer) => buffer.Length > 4 && buffer.GetBuffer()[0] == 'P' && buffer.GetBuffer()[1] == 'K';

    private static string? DependenciesOf(string? fastMcpJson)
    {
        if (fastMcpJson is null)
            return null;
        try
        {
            using var document = JsonDocument.Parse(fastMcpJson);
            if (!document.RootElement.TryGetProperty("environment", out var environment)
                || environment.ValueKind != JsonValueKind.Object
                || !environment.TryGetProperty("dependencies", out var dependencies)
                || dependencies.ValueKind != JsonValueKind.Array)
                return null;
            var lines = ParseDependencies(string.Join('\n', dependencies.EnumerateArray().Where(item => item.ValueKind == JsonValueKind.String).Select(item => item.GetString())));
            return lines.Count == 0 ? null : string.Join('\n', lines);
        }
        catch (JsonException)
        {
            return null;
        }
    }

    private static string FastMcpJson(string? dependencies, string fastMcpVersion)
    {
        using var output = new MemoryStream();
        using (var writer = new Utf8JsonWriter(output, new JsonWriterOptions { Indented = true, Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping }))
        {
            writer.WriteStartObject();
            writer.WriteStartObject("source");
            writer.WriteString("path", MainPath);
            writer.WriteString("entrypoint", "mcp");
            writer.WriteEndObject();
            writer.WriteStartObject("environment");
            writer.WriteString("type", "uv");
            writer.WriteString("python", PythonVersion);
            writer.WriteStartArray("dependencies");
            writer.WriteStringValue($"fastmcp=={fastMcpVersion}");
            foreach (var dependency in ParseDependencies(dependencies))
                writer.WriteStringValue(dependency);
            writer.WriteEndArray();
            writer.WriteEndObject();
            writer.WriteStartObject("deployment");
            writer.WriteString("transport", "http");
            writer.WriteString("host", "0.0.0.0");
            writer.WriteNumber("port", 8000);
            writer.WriteString("path", "/mcp");
            writer.WriteEndObject();
            writer.WriteEndObject();
        }

        return Encoding.UTF8.GetString(output.ToArray()) + "\n";
    }

    private static async Task<byte[]> ReadMainAsync(CancellationToken ct)
    {
        await using var stream = Assembly.GetExecutingAssembly().GetManifestResourceStream(MainResource)
            ?? throw new InvalidOperationException($"Embedded resource {MainResource} is missing.");
        using var output = new MemoryStream();
        await stream.CopyToAsync(output, ct);
        return output.ToArray();
    }

    private static ProjectFileException Unsafe(string path) => new(string.Format(BuilderText.ProjectPathInvalid, path));

    private sealed class ArchiveCollector
    {
        private long _bytes;
        private int _entries;

        public SortedDictionary<string, string> Files { get; } = new(StringComparer.Ordinal);

        public string? FastMcpJson { get; private set; }

        public string? Admit(string rawName, bool directory)
        {
            if (++_entries > MaxArchiveEntries)
                throw new ProjectFileException(string.Format(BuilderText.ArchiveTooManyEntries, MaxArchiveEntries));
            var name = Normalize(rawName, directory);
            return directory || name.Length == 0 ? null : name;
        }

        public bool Wants(string name) => IsKept(name) || name == FastMcpPath;

        public async Task AddAsync(string name, Stream? data, CancellationToken ct)
        {
            var kept = IsKept(name);
            if (kept)
            {
                ValidatePath(name);
                if (Files.ContainsKey(name))
                    throw new ProjectFileException(string.Format(BuilderText.ArchiveDuplicate, name));
            }

            var content = await ReadAsync(data, ct);
            string text;
            try
            {
                text = StrictUtf8.GetString(content);
            }
            catch (DecoderFallbackException)
            {
                throw new ProjectFileException(string.Format(BuilderText.ArchiveNotUtf8, name));
            }

            if (kept)
                Files[name] = text;
            else
                FastMcpJson = text;
        }

        private async Task<byte[]> ReadAsync(Stream? data, CancellationToken ct)
        {
            using var output = new MemoryStream();
            if (data is null)
                return [];
            var chunk = new byte[CopyBufferBytes];
            int read;
            while ((read = await data.ReadAsync(chunk, ct)) > 0)
            {
                _bytes += read;
                if (_bytes > MaxProjectBytes)
                    throw new ProjectFileException(BuilderText.ArchiveTooLarge);
                output.Write(chunk, 0, read);
            }

            return output.ToArray();
        }

        private static bool IsKept(string name) => name is OpenApiPath or BuilderPath || name.StartsWith(ProjectRoot, StringComparison.Ordinal);

        private static string Normalize(string rawName, bool directory)
        {
            var name = rawName;
            while (name.StartsWith("./", StringComparison.Ordinal))
                name = name[2..];
            if (directory || name.EndsWith('/'))
                name = name.TrimEnd('/');
            if (name.Length == 0)
                return name;
            if ( name.StartsWith('/')
                || name.Length > MaxPathLength
                || name.Any(char.IsControl)
                || name.Contains('\\')
                || name.Split('/').Any(segment => segment is "" or "." or ".."))
                throw Unsafe(rawName);
            return name;
        }
    }

    private sealed class LimitedStream(Stream inner, long limit) : Stream
    {
        private long _read;

        public override bool CanRead => true;

        public override bool CanSeek => false;

        public override bool CanWrite => false;

        public override long Length => throw new NotSupportedException();

        public override long Position
        {
            get => throw new NotSupportedException();
            set => throw new NotSupportedException();
        }

        public override int Read(byte[] buffer, int offset, int count) => Count(inner.Read(buffer, offset, count));

        public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default) =>
            Count(await inner.ReadAsync(buffer, cancellationToken));

        public override void Flush()
        {
        }

        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();

        public override void SetLength(long value) => throw new NotSupportedException();

        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();

        private int Count(int read)
        {
            _read += read;
            if (_read > limit)
                throw new ProjectFileException(BuilderText.ArchiveTooLarge);
            return read;
        }
    }
}
