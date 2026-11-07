namespace CtxUi.Extensions.Testing;

public sealed record TestPanelOptions(
    string ExtensionsDir,
    IReadOnlyDictionary<string, string>? Settings = null,
    string Language = "ru");
