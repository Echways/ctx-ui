using System.Collections.Concurrent;

namespace CtxUi.Gateway;

/// <summary>Решает, пора ли записать обращение клиента в базу, чтобы не писать на каждый запрос.</summary>
public sealed class TouchThrottle(TimeSpan interval)
{
    private readonly ConcurrentDictionary<Guid, DateTimeOffset> _lastTouch = new();

    public bool ShouldTouch(Guid clientId, DateTimeOffset now)
    {
        while (true)
        {
            if (!_lastTouch.TryGetValue(clientId, out var previous))
            {
                if (_lastTouch.TryAdd(clientId, now))
                    return true;

                continue;
            }

            if (now - previous < interval)
                return false;
            if (_lastTouch.TryUpdate(clientId, now, previous))
                return true;
        }
    }
}
