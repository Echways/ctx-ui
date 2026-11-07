using System.Text.Json.Nodes;
using CtxUi.Builder.Components;
using CtxUi.Builder.Tools;

namespace CtxUi.Builder.Tests;

public sealed class ToolFormDraftTests
{
    [Theory]
    [InlineData("integer", "3", "3")]
    [InlineData("number", "1.5", "1.5")]
    [InlineData("boolean", "true", "true")]
    [InlineData("string", "Berlin", "\"Berlin\"")]
    [InlineData("array", "[1,2]", "[1,2]")]
    public void Default_text_becomes_a_value_of_the_parameter_type(string type, string text, string json)
    {
        var parameter = new ParameterRow { Name = "p", Type = type, DefaultText = text }.ToParameter();

        Assert.Equal(json, parameter.Default!.ToJsonString());
    }

    [Theory]
    [InlineData("integer", "three")]
    [InlineData("boolean", "maybe")]
    [InlineData("object", "[1]")]
    [InlineData("array", "{broken")]
    public void Default_text_that_does_not_fit_the_type_is_refused(string type, string text)
    {
        var row = new ParameterRow { Name = "p", Type = type, DefaultText = text };

        Assert.Throws<BuilderException>(row.ToParameter);
    }

    [Fact]
    public void Path_parameters_are_always_required_and_an_empty_default_is_none()
    {
        var parameter = new ParameterRow { Name = "id", Place = ParameterPlace.Path, DefaultText = "  " }.ToParameter();

        Assert.True(parameter.Required);
        Assert.Null(parameter.Default);
    }

    [Fact]
    public void A_tool_survives_the_trip_through_the_draft()
    {
        var tool = new FormTool("get_forecast", "GET", "/f/{city}", "Forecast",
            [
                new ToolParameter("city", "string", ParameterPlace.Path, true, null, "City"),
                new ToolParameter("days", "integer", ParameterPlace.Query, false, JsonValue.Create(3), null)
            ],
            JsonNode.Parse("""{"type":"object"}"""));

        var back = ToolFormDraft.From(tool).ToTool();

        Assert.Equal(OpenApiDraft.Upsert(string.Empty, tool), OpenApiDraft.Upsert(string.Empty, back));
    }

    [Fact]
    public void An_empty_description_is_flagged_but_allowed()
    {
        var draft = new ToolFormDraft { Name = "t", Description = "  " };

        Assert.True(draft.LacksDescription);
        Assert.Null(draft.ToTool().Description);
    }
}
