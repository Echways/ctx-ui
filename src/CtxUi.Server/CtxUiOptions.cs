namespace CtxUi.Server;

public sealed class CtxUiOptions
{
    public const string Section = "CtxUi";

    /// <summary>Каталог с базой и ключами шифрования.</summary>
    public string DataDir { get; set; } = "data";

    /// <summary>Внешний адрес шлюза. С него начинаются адреса, которые CLI показывает клиенту.</summary>
    public string PublicUrl { get; set; } = "http://localhost:8080";
}
