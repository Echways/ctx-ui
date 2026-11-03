namespace CtxUi.Builder.Components;

public sealed class NewServerDraft
{
    public string Name { get; set; } = "";

    public string Slug { get; set; } = "";

    public string? ImportAddress { get; set; }

    public string? ImportPaste { get; set; }
}
