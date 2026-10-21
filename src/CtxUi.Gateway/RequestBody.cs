using System.Buffers;
using Microsoft.AspNetCore.Http;

namespace CtxUi.Gateway;

internal enum BodyStatus
{
    Read,
    TooLarge,
    Aborted,
}

internal readonly record struct BufferedBody(BodyStatus Status, ArraySegment<byte> Bytes);

internal static class RequestBody
{
    private const int ChunkSize = 16 * 1024;

    private const int InitialCapacityCeiling = 64 * 1024;

    public static async Task<BufferedBody> ReadAsync(HttpRequest request, int limit, CancellationToken ct)
    {
        if (request.ContentLength > limit)
            return new BufferedBody(BodyStatus.TooLarge, []);

        using var buffer = new MemoryStream((int)Math.Min(request.ContentLength ?? 0, InitialCapacityCeiling));
        var chunk = ArrayPool<byte>.Shared.Rent(ChunkSize);
        try
        {
            int read;
            while ((read = await request.Body.ReadAsync(chunk, ct)) > 0)
            {
                if (buffer.Length + read > limit)
                    return new BufferedBody(BodyStatus.TooLarge, []);

                buffer.Write(chunk, 0, read);
            }
        }
        catch (Exception exception) when (exception is IOException or OperationCanceledException)
        {
            return new BufferedBody(BodyStatus.Aborted, []);
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(chunk);
        }

        buffer.TryGetBuffer(out var bytes);
        return new BufferedBody(BodyStatus.Read, bytes);
    }
}
