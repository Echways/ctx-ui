namespace CtxUi.Builder.Components;

public sealed class ConnectDraft
{
    public string Slug { get; set; } = "";

    public string Name { get; set; } = "";

    public bool ForwardIdentity { get; set; }

    public HashSet<Guid> Clients { get; } = [];
}
