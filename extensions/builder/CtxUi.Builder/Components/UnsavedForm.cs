using System.Text.Json;

namespace CtxUi.Builder.Components;

public interface IUnsavedForm
{
    bool HasUnsavedChanges { get; }
}

public static class DraftSnapshot
{
    public static string Of<T>(T draft) => JsonSerializer.Serialize(draft);
}
