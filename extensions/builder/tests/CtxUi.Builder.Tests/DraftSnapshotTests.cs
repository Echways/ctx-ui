using CtxUi.Builder.Components;
using CtxUi.Builder.Tools;

namespace CtxUi.Builder.Tests;

public sealed class DraftSnapshotTests
{
    [Fact]
    public void Untouched_drafts_have_equal_snapshots()
    {
        var resource = new BuilderResource("data://cities", "cities", "text/plain", "Paris");
        var prompt = new BuilderPrompt("report", null, "Report on {city}");

        Assert.Equal(DraftSnapshot.Of(ResourceFormDraft.From(resource)),
            DraftSnapshot.Of(ResourceFormDraft.From(resource)));
        Assert.Equal(DraftSnapshot.Of(PromptFormDraft.From(prompt)), DraftSnapshot.Of(PromptFormDraft.From(prompt)));
        Assert.Equal(DraftSnapshot.Of(new ToolFormDraft()), DraftSnapshot.Of(new ToolFormDraft()));
    }

    [Fact]
    public void Any_edit_changes_the_snapshot()
    {
        var tool = new ToolFormDraft();
        var before = DraftSnapshot.Of(tool);

        tool.Parameters.Add(new ParameterRow());
        var withParameter = DraftSnapshot.Of(tool);
        tool.Parameters[0].Name = "city";

        Assert.NotEqual(before, withParameter);
        Assert.NotEqual(withParameter, DraftSnapshot.Of(tool));
    }
}
