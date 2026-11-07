namespace CtxUi.Builder.BlackBoxTests;

public sealed class RunnerFixture : IAsyncDisposable
{
    internal RunnerStack Stack { get; } = new();

    public ValueTask DisposeAsync()
    {
        return Stack.DisposeAsync();
    }
}
