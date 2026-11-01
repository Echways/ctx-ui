namespace CtxUi.Extensions;

public interface IExtensionInitializer
{
    Task InitializeAsync(CancellationToken ct);
}
