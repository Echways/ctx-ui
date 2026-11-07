using Microsoft.AspNetCore.Components;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using Enfilade;
using CtxUi.Extensions;

namespace CtxUi.Panel.Tests;

public class PanelLayoutTests
{
    [Route("/sample")]
    private sealed class SamplePage : ComponentBase;

    [Route("/sample/second")]
    private sealed class SecondPage : ComponentBase;

    [Route("/sample/third")]
    private sealed class ThirdPage : ComponentBase;


    private static PanelEditor Editor(PanelLayout layout)
    {
        return layout.EditorFor("SampleAssembly");
    }

    private static EnfiladeOptions Applied(PanelLayout layout)
    {
        var services = new ServiceCollection();
        layout.Apply(new EnfiladeBuilder(services));
        return services.BuildServiceProvider().GetRequiredService<IOptions<EnfiladeOptions>>().Value;
    }

    [Fact]
    public void The_default_order_matches_the_panel_today()
    {
        Assert.Equal(
            ["overview", "metrics", "inbounds", "clients", "journal", "access", "oauth", "keys"],
            PanelServices.CreateLayout().VisibleEntries);
    }

    [Fact]
    public void A_new_section_stands_before_the_access_caption_by_default()
    {
        var layout = PanelServices.CreateLayout();

        Editor(layout).AddSection<SamplePage>(() => "Sample");

        Assert.Equal(
            ["overview", "metrics", "inbounds", "clients", "journal", "sample", "access", "oauth", "keys"],
            layout.VisibleEntries);
    }

    [Fact]
    public void Consecutive_additions_keep_their_order()
    {
        var layout = PanelServices.CreateLayout();

        Editor(layout)
            .AddCaption(() => "Sample caption")
            .AddSection<SamplePage>(() => "Sample")
            .AddSection<SecondPage>(() => "Second")
            .AddSection<ThirdPage>(() => "Third");

        var entries = Applied(layout).Navigation;
        var access = entries.OfType<Caption>().ToList();
        Assert.Equal(2, access.Count);
        Assert.Equal("Sample caption", access[0].Text());
        var start = entries.IndexOf(access[0]);
        Assert.Equal(
            [typeof(SamplePage), typeof(SecondPage), typeof(ThirdPage)],
            entries.Skip(start + 1).Take(3).OfType<Section>().Select(section => section.Page));
        Assert.Equal(access[1], entries[start + 4]);
    }

    [Fact]
    public void Before_and_after_place_a_section_next_to_its_target()
    {
        var layout = PanelServices.CreateLayout();

        Editor(layout).AddSection<SamplePage>(() => "Sample", PanelPosition.After(PanelSections.Overview));
        Editor(layout).AddSection<SecondPage>(() => "Second", PanelPosition.Before(PanelSections.ApiKeys));

        Assert.Equal(
            [
                "overview", "sample", "metrics", "inbounds", "clients", "journal", "access", "oauth", "sample/second",
                "keys"
            ],
            layout.VisibleEntries);
    }

    [Fact]
    public void Move_rename_and_hide_change_built_in_entries()
    {
        var layout = PanelServices.CreateLayout();

        Editor(layout)
            .Move(PanelSections.Journal, PanelPosition.Before(PanelSections.Overview))
            .Rename(PanelSections.Clients, () => "Customers")
            .Hide(PanelSections.Metrics);

        Assert.Equal(
            ["journal", "overview", "inbounds", "clients", "access", "oauth", "keys"],
            layout.VisibleEntries);
        var titles = Applied(layout).Navigation.OfType<Section>().Select(section => section.Title());
        Assert.Contains("Customers", titles);
    }

    [Fact]
    public void A_hidden_section_keeps_its_route_reachable()
    {
        var layout = PanelServices.CreateLayout();

        Editor(layout).AddSection<SamplePage>(() => "Sample").Hide(PanelSections.Of<SamplePage>());

        var options = Applied(layout);
        Assert.DoesNotContain(options.Navigation.OfType<Section>(), section => section.Page == typeof(SamplePage));
        Assert.Contains(typeof(SamplePage).Assembly, options.Assemblies);
    }

    [Fact]
    public void A_caption_with_no_sections_left_disappears()
    {
        var layout = PanelServices.CreateLayout();

        Editor(layout).Hide(PanelSections.OAuth).Hide(PanelSections.ApiKeys);

        Assert.Equal(["overview", "metrics", "inbounds", "clients", "journal"], layout.VisibleEntries);
    }

    [Fact]
    public void An_unknown_target_is_an_error_naming_it()
    {
        var layout = PanelServices.CreateLayout();

        var hide = Assert.Throws<InvalidOperationException>(() => Editor(layout).Hide("nowhere"));
        var place = Assert.Throws<InvalidOperationException>(() =>
            Editor(layout).AddSection<SamplePage>(() => "Sample", PanelPosition.After("elsewhere")));

        Assert.Contains("nowhere", hide.Message);
        Assert.Contains("elsewhere", place.Message);
    }

    [Fact]
    public void A_stylesheet_of_an_extension_comes_after_the_panel_one_under_its_assembly()
    {
        var layout = PanelServices.CreateLayout();

        Editor(layout).AddStylesheet("sample.css");

        Assert.Equal(
            ["_content/CtxUi.Panel/panel.css", "_content/SampleAssembly/sample.css"],
            Applied(layout).Stylesheets);
    }

    [Fact]
    public void Customize_changes_only_the_look_it_sets()
    {
        var layout = PanelServices.CreateLayout();

        Editor(layout).Customize(look =>
        {
            look.Brand = "Acme";
            look.Accent = "#112233";
        });

        var options = Applied(layout);
        Assert.Equal("Acme", options.Brand);
        Assert.Equal("#112233", options.Accent);
        Assert.Null(options.Favicon);
    }

    [Theory]
    [InlineData("nothing")]
    [InlineData(PanelSections.AccessCaption)]
    public void A_page_slot_of_something_that_is_not_a_built_in_page_is_refused(string section)
    {
        var failure = Assert.Throws<ArgumentException>(() => PanelSlots.PageTop(section));

        Assert.Contains(section, failure.Message, StringComparison.Ordinal);
    }
}
