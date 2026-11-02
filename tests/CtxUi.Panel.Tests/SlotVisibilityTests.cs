using CtxUi.Extensions;

namespace CtxUi.Panel.Tests;

public class SlotVisibilityTests
{
    private const string Slot = "inbound-actions";

    private static SlotAction<string> Action(string title, Func<string, bool>? visible = null) =>
        new(() => title, _ => Task.CompletedTask, 0, visible);

    private static PanelSlotRegistry Registry(params SlotAction<string>[] actions)
    {
        var registry = new PanelSlotRegistry();
        foreach (var action in actions)
            registry.AddAction(Slot, action);
        registry.Freeze();
        return registry;
    }

    private static string[] Titles(IEnumerable<SlotAction<string>> actions) => [.. actions.Select(action => action.Title())];

    [Fact]
    public void Actions_without_a_predicate_are_always_shown_and_the_row_is_not_built()
    {
        var registry = Registry(Action("always"));
        var built = false;

        var visible = registry.VisibleActions<string>(Slot, () =>
        {
            built = true;
            return "row";
        }, _ => { });

        Assert.Equal(["always"], Titles(visible));
        Assert.False(built);
    }

    [Fact]
    public void A_predicate_decides_per_row()
    {
        var registry = Registry(Action("always"), Action("built only", row => row == "built"));

        Assert.Equal(["always", "built only"], Titles(registry.VisibleActions<string>(Slot, () => "built", _ => { })));
        Assert.Equal(["always"], Titles(registry.VisibleActions<string>(Slot, () => "other", _ => { })));
    }

    [Fact]
    public void A_predicate_that_throws_hides_its_action_and_is_reported()
    {
        var registry = Registry(Action("always"), Action("broken", _ => throw new InvalidOperationException("boom")));
        var failures = new List<Exception>();

        var visible = registry.VisibleActions<string>(Slot, () => "row", failures.Add);

        Assert.Equal(["always"], Titles(visible));
        Assert.Equal("boom", Assert.Single(failures).Message);
    }

    [Fact]
    public void A_row_that_cannot_be_built_hides_only_the_conditional_actions()
    {
        var registry = Registry(Action("always"), Action("conditional", _ => true));
        var failures = new List<Exception>();

        var visible = registry.VisibleActions<string>(Slot, () => throw new InvalidOperationException("no row"), failures.Add);

        Assert.Equal(["always"], Titles(visible));
        Assert.Single(failures);
    }
}
