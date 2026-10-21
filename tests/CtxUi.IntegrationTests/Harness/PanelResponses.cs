
namespace CtxUi.IntegrationTests;

internal static class PanelResponses
{
    public const string Refusal = "Неверный логин или пароль";

    public static string Cookie(HttpResponseMessage response) =>
        response.Headers.TryGetValues("Set-Cookie", out var values)
            ? values.SingleOrDefault(value => value.StartsWith("ctx-ui=", StringComparison.Ordinal)) ?? ""
            : "";
}
