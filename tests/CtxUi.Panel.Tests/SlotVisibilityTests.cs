using System.Reflection;
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

    [Fact]
    public void When_scopes_the_registered_action_to_its_predicate()
    {
        var registry = new PanelSlotRegistry();
        var editor = new PanelActionSlotEditor<string>(null!, registry, Slot);
        editor.AddAction(() => "always", _ => Task.CompletedTask);
        editor.When(row => row == "built").AddAction(() => "built only", _ => Task.CompletedTask);
        editor.When(row => row == "built").AddSheet<Sheet>(() => "sheet", row => row);
        registry.Freeze();

        Assert.Equal(["always", "built only", "sheet"], Titles(registry.VisibleActions<string>(Slot, () => "built", _ => { })));
        Assert.Equal(["always"], Titles(registry.VisibleActions<string>(Slot, () => "other", _ => { })));
    }

    [Fact]
    public void The_shipped_action_and_sheet_signatures_stay_binary_compatible()
    {
        var editor = typeof(PanelActionSlotEditor<>);
        var action = editor.GetMethods().Where(method => method.Name == "AddAction").Select(Parameters);
        var sheet = editor.GetMethods().Where(method => method.Name == "AddSheet").Select(Parameters);

        Assert.Contains(["Func`1", "Func`2", "Int32"], action, new SequenceComparer());
        Assert.Contains(["Func`1", "Func`2", "Int32"], sheet, new SequenceComparer());
    }

    private static string[] Parameters(MethodInfo method) => [.. method.GetParameters().Select(parameter => parameter.ParameterType.Name)];

    private sealed class SequenceComparer : IEqualityComparer<string[]>
    {
        public bool Equals(string[]? x, string[]? y) => x is not null && y is not null && x.SequenceEqual(y);

        public int GetHashCode(string[] value) => value.Length;
    }

    private sealed class Sheet : PanelBlock<string>
    {
    }
}
