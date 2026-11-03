using System.Text.Json;
using CtxUi.Builder.Components;
using Enfilade;

namespace CtxUi.Builder.Tests;

public sealed class ToolArgumentsTests
{
    private static ArgumentSet From(string schema) => ArgumentSet.From(JsonDocument.Parse(schema).RootElement);

    [Fact]
    public void Simple_properties_become_typed_fields_and_nested_ones_take_json()
    {
        var set = From("""
            {"type":"object","required":["city"],"properties":{
              "city":{"type":"string","description":"City name"},
              "days":{"type":"integer","default":3},
              "metric":{"type":"boolean"},
              "ratio":{"type":"number"},
              "tags":{"type":"array","items":{"type":"string"}},
              "filter":{"type":"object"},
              "note":{"anyOf":[{"type":"string"},{"type":"null"}]},
              "either":{"anyOf":[{"type":"string"},{"type":"integer"}]}}}
            """);

        Assert.Equal(
            [("city", "string", true), ("days", "integer", false), ("metric", "boolean", false), ("ratio", "number", false), ("tags", "json", false), ("filter", "json", false), ("note", "string", false), ("either", "json", false)],
            set.Fields.Select(field => (field.Name, field.Type, field.Required)));
        Assert.Equal("City name", set.Fields[0].Description);
        Assert.Equal("3", set.Fields[1].Text);
    }

    [Fact]
    public void Entered_values_are_typed_and_empty_optional_fields_are_left_out()
    {
        var set = From("""{"type":"object","required":["city"],"properties":{"city":{"type":"string"},"days":{"type":"integer"},"ratio":{"type":"number"},"metric":{"type":"boolean"},"tags":{"type":"array"},"skipped":{"type":"string"}}}""");
        Fill(set, ("city", "Berlin"), ("days", "5"), ("ratio", "0.5"), ("metric", "true"), ("tags", "[\"a\",\"b\"]"));

        var arguments = set.ToArguments();

        Assert.Equal("""{"city":"Berlin","days":5,"ratio":0.5,"metric":true,"tags":["a","b"]}""", arguments.ToJsonString());
    }

    [Fact]
    public void Required_text_may_be_empty_but_other_required_values_may_not()
    {
        var text = From("""{"type":"object","required":["name"],"properties":{"name":{"type":"string"}}}""");
        var number = From("""{"type":"object","required":["count"],"properties":{"count":{"type":"integer"}}}""");

        Assert.Equal("""{"name":""}""", text.ToArguments().ToJsonString());
        Assert.Throws<PanelException>(() => number.ToArguments());
    }

    [Theory]
    [InlineData("integer", "x")]
    [InlineData("integer", "1.5")]
    [InlineData("number", "abc")]
    [InlineData("boolean", "yes")]
    [InlineData("array", "{")]
    public void Invalid_values_are_refused_with_the_field_name(string type, string value)
    {
        var set = From("""{"type":"object","properties":{"field":{"type":"TYPE"}}}""".Replace("TYPE", type));
        Fill(set, ("field", value));

        var failure = Assert.Throws<PanelException>(() => set.ToArguments());

        Assert.Contains("field", failure.Message);
    }

    [Fact]
    public void Schema_without_properties_gives_no_fields()
    {
        Assert.Empty(From("{}").Fields);
        Assert.Empty(From("""{"type":"object"}""").ToArguments());
    }

    private static void Fill(ArgumentSet set, params (string Name, string Value)[] values)
    {
        foreach (var (name, value) in values)
            set.Fields.Single(field => field.Name == name).Text = value;
    }
}
