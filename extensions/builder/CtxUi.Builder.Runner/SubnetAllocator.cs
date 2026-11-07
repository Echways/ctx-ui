using System.Net;
using System.Net.Sockets;

namespace CtxUi.Builder.Runner;

public static class SubnetAllocator
{
    private const int BlockPrefix = 28;
    private const uint BlockSize = 1u << (32 - BlockPrefix);

    public static string Next(string pool, IReadOnlyCollection<string> taken)
    {
        var (poolStart, poolEnd) = Range(pool) ??
                                   throw new ArgumentException($"Subnet pool '{pool}' is not an IPv4 network.",
                                       nameof(pool));
        if (poolEnd - poolStart + 1 < BlockSize)
            throw new ArgumentException($"Subnet pool '{pool}' is smaller than a /{BlockPrefix} network.",
                nameof(pool));

        var occupied = taken.Select(Range).OfType<(uint Start, uint End)>().ToList();
        for (var start = poolStart; start <= poolEnd - (BlockSize - 1); start += BlockSize)
        {
            var end = start + BlockSize - 1;
            if (!occupied.Any(range => range.Start <= end && start <= range.End))
                return $"{ToAddress(start)}/{BlockPrefix}";

            if (end == uint.MaxValue)
                break;
        }

        throw new InvalidOperationException(
            $"The subnet pool {pool} has no free /{BlockPrefix} network left. Enlarge the pool or remove unused servers.");
    }

    private static (uint Start, uint End)? Range(string cidr)
    {
        var parts = cidr.Split('/');
        if (parts.Length != 2
            || !IPAddress.TryParse(parts[0], out var address)
            || address.AddressFamily != AddressFamily.InterNetwork
            || !int.TryParse(parts[1], out var prefix)
            || prefix is < 0 or > 32)
            return null;

        var value = System.Buffers.Binary.BinaryPrimitives.ReadUInt32BigEndian(address.GetAddressBytes());
        var mask = prefix == 0 ? 0u : uint.MaxValue << (32 - prefix);
        var start = value & mask;
        return (start, start | ~mask);
    }

    private static IPAddress ToAddress(uint value)
    {
        Span<byte> bytes = stackalloc byte[4];
        System.Buffers.Binary.BinaryPrimitives.WriteUInt32BigEndian(bytes, value);
        return new IPAddress(bytes);
    }
}
