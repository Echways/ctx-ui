namespace CtxUi.Gateway;

public sealed class GatewayOptions
{
    /// <summary>Как часто сервер проверяет, не изменил ли базу другой процесс.</summary>
    public TimeSpan RefreshInterval { get; set; } = TimeSpan.FromSeconds(1);
}
