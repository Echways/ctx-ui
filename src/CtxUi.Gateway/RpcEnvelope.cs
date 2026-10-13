using System.Text;
using System.Text.Json;
using CtxUi.Core.Usage;

namespace CtxUi.Gateway;

public sealed record RpcCall(string? Id, string? Method, string? Tool)
{
    public bool IsToolCall => Method == UsageRecord.ToolsCall;
}

public sealed record RpcEnvelope(IReadOnlyList<RpcCall> Calls, bool IsBatch)
{
    public const int MaxBatch = 100;
    public const int MaxNameLength = 128;

    private const int MaxDepth = 256;

    public int ToolCalls => Calls.Count(call => call.IsToolCall);

    public static RpcEnvelope? Read(ReadOnlySpan<byte> body)
    {
        try
        {
            var reader = new Utf8JsonReader(body, new JsonReaderOptions { MaxDepth = MaxDepth });
            if (!reader.Read())
                return null;

            var calls = new List<RpcCall>();
            var batch = reader.TokenType == JsonTokenType.StartArray;
            if (batch)
            {
                while (reader.Read() && reader.TokenType != JsonTokenType.EndArray)
                {
                    if (calls.Count == MaxBatch || ReadMessage(ref reader, body) is not { } call)
                        return null;

                    calls.Add(call);
                }
            }
            else if (ReadMessage(ref reader, body) is { } single)
            {
                calls.Add(single);
            }

            return calls.Count == 0 || reader.Read() ? null : new RpcEnvelope(calls, batch);
        }
        catch (JsonException)
        {
            return null;
        }
    }

    private static RpcCall? ReadMessage(ref Utf8JsonReader reader, ReadOnlySpan<byte> body)
    {
        if (reader.TokenType != JsonTokenType.StartObject)
            return null;

        string? id = null;
        string? method = null;
        string? name = null;
        var seenId = false;
        var seenMethod = false;
        var seenParams = false;
        while (reader.Read() && reader.TokenType == JsonTokenType.PropertyName)
        {
            if (reader.ValueTextEquals("id"u8))
            {
                if (seenId || !reader.Read())
                    return null;

                seenId = true;
                if (reader.TokenType is JsonTokenType.String or JsonTokenType.Number)
                    id = Encoding.UTF8.GetString(body[(int)reader.TokenStartIndex..(int)reader.BytesConsumed]);
                else if (reader.TokenType != JsonTokenType.Null)
                    return null;
            }
            else if (reader.ValueTextEquals("method"u8))
            {
                if (seenMethod || !reader.Read() || reader.TokenType != JsonTokenType.String)
                    return null;

                seenMethod = true;
                method = reader.GetString();
            }
            else if (reader.ValueTextEquals("params"u8))
            {
                if (seenParams || !reader.Read())
                    return null;

                seenParams = true;
                if (reader.TokenType != JsonTokenType.StartObject)
                    reader.Skip();
                else if (!ReadName(ref reader, out name))
                    return null;
            }
            else
            {
                reader.Skip();
            }
        }

        var printableMethod = Printable(method);
        return new RpcCall(id, printableMethod, printableMethod == UsageRecord.ToolsCall ? Printable(name) : null);
    }

    private static bool ReadName(ref Utf8JsonReader reader, out string? name)
    {
        name = null;
        var seen = false;
        while (reader.Read() && reader.TokenType == JsonTokenType.PropertyName)
        {
            if (!reader.ValueTextEquals("name"u8))
            {
                reader.Skip();
                continue;
            }

            if (seen || !reader.Read())
                return false;

            seen = true;
            if (reader.TokenType == JsonTokenType.String)
                name = reader.GetString();
            else
                reader.Skip();
        }

        return true;
    }

    private static string? Printable(string? value)
    {
        if (value is null)
            return null;

        var text = value.Length > MaxNameLength ? value[..MaxNameLength] : value;
        return text.Any(char.IsControl) ? string.Concat(text.Select(symbol => char.IsControl(symbol) ? '?' : symbol)) : text;
    }
}
