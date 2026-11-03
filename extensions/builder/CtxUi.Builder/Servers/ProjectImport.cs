namespace CtxUi.Builder.Servers;

public sealed record ProjectImport(IReadOnlyList<(string Path, string Content)> Files, string? Dependencies);
