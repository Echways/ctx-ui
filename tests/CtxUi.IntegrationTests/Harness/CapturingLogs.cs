using System.Collections.Concurrent;
using Microsoft.Extensions.Logging;

namespace CtxUi.IntegrationTests;

public sealed class CapturingLogs : ILoggerProvider
{
    public ConcurrentQueue<string> Lines { get; } = new();

    public ILogger CreateLogger(string categoryName) => new Sink(categoryName, Lines);

    public void Dispose()
    {
    }

    private sealed class Sink(string category, ConcurrentQueue<string> lines) : ILogger
    {
        public IDisposable? BeginScope<TState>(TState state)
            where TState : notnull => null;

        public bool IsEnabled(LogLevel logLevel) => true;

        public void Log<TState>(
            LogLevel logLevel,
            EventId eventId,
            TState state,
            Exception? exception,
            Func<TState, Exception?, string> formatter) =>
            lines.Enqueue($"{logLevel} {category}: {formatter(state, exception)} {exception}");
    }
}
