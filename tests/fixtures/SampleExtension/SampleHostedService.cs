using Microsoft.Extensions.Hosting;

namespace SampleExtension;

public sealed class SampleStatus
{
    public string Value { get; set; } = "waiting";
}

public sealed class SampleHostedService(SampleStatus status) : BackgroundService
{
    protected override Task ExecuteAsync(CancellationToken stoppingToken)
    {
        status.Value = "started";
        return Task.CompletedTask;
    }
}

public sealed class FailingInitializer : CtxUi.Extensions.IExtensionInitializer
{
    public Task InitializeAsync(CancellationToken ct) => throw new InvalidOperationException("Sample initializer failure");
}
