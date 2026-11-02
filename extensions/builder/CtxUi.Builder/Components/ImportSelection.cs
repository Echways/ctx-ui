namespace CtxUi.Builder.Components;

public sealed class ImportSelection
{
    public HashSet<string> Names { get; } = new(StringComparer.Ordinal);

    public bool UseBaseUrl { get; set; }
}
