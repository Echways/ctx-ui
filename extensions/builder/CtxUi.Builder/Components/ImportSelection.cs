using CtxUi.Builder.Tools;

namespace CtxUi.Builder.Components;

public sealed class ImportSelection
{
    public HashSet<string> Names { get; } = new(StringComparer.Ordinal);

    public bool UseBaseUrl { get; set; }

    public static ImportSelection Default(ImportedApi api, string? currentBaseUrl) =>
        new() { UseBaseUrl = string.IsNullOrWhiteSpace(currentBaseUrl) && api.BaseUrl is not null };
}
