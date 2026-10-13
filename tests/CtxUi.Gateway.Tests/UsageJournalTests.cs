using System.Collections.Concurrent;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using CtxUi.Core.Data;
using CtxUi.Core.Usage;

namespace CtxUi.Gateway.Tests;

public sealed class UsageJournalTests : IAsyncDisposable
{
    private static readonly DateTimeOffset Now = new(2026, 10, 12, 10, 0, 0, TimeSpan.Zero);
    private static readonly Guid Client = Guid.NewGuid();
    private static readonly Guid Inbound = Guid.NewGuid();

    private readonly SqliteConnection _connection = new("DataSource=:memory:");
    private readonly Warnings _warnings = new();
    private readonly ServiceProvider _services;
    private readonly UsageJournal _journal;

    public UsageJournalTests()
    {
        _connection.Open();
        _services = new ServiceCollection()
            .AddDbContext<CtxUiDbContext>(options => options.UseSqlite(_connection))
            .AddScoped<UsageStore>()
            .BuildServiceProvider();
        using (var scope = _services.CreateScope())
            scope.ServiceProvider.GetRequiredService<CtxUiDbContext>().Database.Migrate();

        _journal = new UsageJournal(_services.GetRequiredService<IServiceScopeFactory>(), _warnings);
    }

    private static UsageRecord Call(string tool) => new(Client, Inbound, Now, "tools/call", tool, 10, UsageOutcome.Ok);

    private static UsageRecord Refusal() => new(Client, Inbound, Now, null, null, 0, UsageOutcome.RateLimited);

    private async Task<T> ReadAsync<T>(Func<UsageStore, Task<T>> read)
    {
        await using var scope = _services.CreateAsyncScope();
        return await read(scope.ServiceProvider.GetRequiredService<UsageStore>());
    }

    public async ValueTask DisposeAsync()
    {
        await _services.DisposeAsync();
        await _connection.DisposeAsync();
    }

    [Fact]
    public async Task Nothing_is_written_until_the_flush()
    {
        _journal.Record(Call("a"));

        var before = await ReadAsync(store => store.RecentAsync(null, 10));
        await _journal.FlushAsync();
        var after = await ReadAsync(store => store.RecentAsync(null, 10));

        Assert.Empty(before);
        Assert.Equal("a", Assert.Single(after).Tool);
    }

    [Fact]
    public async Task Flush_writes_everything_that_piled_up_in_the_order_it_came()
    {
        for (var number = 0; number < 1_234; number++)
            _journal.Record(Call(number.ToString()));

        await _journal.FlushAsync();
        await _journal.FlushAsync();

        var lines = await ReadAsync(store => store.RecentAsync(null, 5_000));
        Assert.Equal(1_234, lines.Count);
        Assert.Equal("1233", lines[0].Tool);
        Assert.Equal("0", lines[^1].Tool);
        Assert.Empty(_warnings.Lines);
    }

    [Fact]
    public async Task Overflow_drops_new_records_says_how_many_and_the_journal_keeps_working()
    {
        for (var number = 0; number < UsageJournal.Capacity + 5; number++)
            _journal.Record(Refusal());

        await _journal.FlushAsync();
        _journal.Record(Call("after"));
        await _journal.FlushAsync();

        var day = Assert.Single(await ReadAsync(store => store.DailyAsync(null, new DateOnly(2026, 10, 12))));
        Assert.Equal(UsageJournal.Capacity, day.Refused);
        Assert.Equal(1, day.Calls);
        Assert.Contains("пропущено записей: 5", Assert.Single(_warnings.Lines));
    }

    private sealed class Warnings : ILogger<UsageJournal>
    {
        public ConcurrentQueue<string> Lines { get; } = new();

        public IDisposable? BeginScope<TState>(TState state)
            where TState : notnull => null;

        public bool IsEnabled(LogLevel logLevel) => true;

        public void Log<TState>(
            LogLevel logLevel,
            EventId eventId,
            TState state,
            Exception? exception,
            Func<TState, Exception?, string> formatter) =>
            Lines.Enqueue(formatter(state, exception));
    }
}
