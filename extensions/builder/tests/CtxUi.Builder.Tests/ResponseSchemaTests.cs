using CtxUi.Builder.Tools;
using Enfilade;

namespace CtxUi.Builder.Tests;

public sealed class ResponseSchemaTests
{
    [Fact]
    public void Schema_from_example_describes_the_nested_object()
    {
        var schema = ResponseSchema.FromExample("""{"city":"Berlin","forecast":{"temp":11.5,"days":[1,2,3]}}""");

        Assert.Equal("object", schema["type"]!.GetValue<string>());
        Assert.Equal("string", schema["properties"]!["city"]!["type"]!.GetValue<string>());
        var forecast = schema["properties"]!["forecast"]!;
        Assert.Equal("object", forecast["type"]!.GetValue<string>());
        Assert.Equal("number", forecast["properties"]!["temp"]!["type"]!.GetValue<string>());
        Assert.Equal("array", forecast["properties"]!["days"]!["type"]!.GetValue<string>());
        Assert.Null(schema["$schema"]);
    }

    [Theory]
    [InlineData("{broken")]
    [InlineData("")]
    public void Invalid_example_is_refused_with_a_message(string example)
    {
        var failure = Assert.Throws<PanelException>(() => ResponseSchema.FromExample(example));

        Assert.Equal(example.Length == 0 ? BuilderText.ResponseExampleEmpty : BuilderText.ResponseExampleInvalid, failure.Message);
    }
}
