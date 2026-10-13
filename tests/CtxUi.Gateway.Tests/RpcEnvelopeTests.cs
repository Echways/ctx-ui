using System.Text;

namespace CtxUi.Gateway.Tests;

public class RpcEnvelopeTests
{
    private static RpcEnvelope? Read(string json) => RpcEnvelope.Read(Encoding.UTF8.GetBytes(json));

    private static RpcCall Single(string json)
    {
        var envelope = Read(json);
        Assert.NotNull(envelope);
        Assert.False(envelope.IsBatch);
        return Assert.Single(envelope.Calls);
    }

    [Fact]
    public void Tool_call_gives_id_method_and_tool_name()
    {
        var call = Single("""{"jsonrpc":"2.0","id":7,"method":"tools/call","params":{"name":"echo","arguments":{"text":"привет"}}}""");

        Assert.Equal(new RpcCall("7", "tools/call", "echo"), call);
        Assert.True(call.IsToolCall);
    }

    [Theory]
    [InlineData("""{"id":"abc-1","method":"ping"}""", "\"abc-1\"")]
    [InlineData("""{"id":"ква\"дро","method":"ping"}""", "\"ква\\\"дро\"")]
    [InlineData("""{"id":1.5e3,"method":"ping"}""", "1.5e3")]
    [InlineData("""{"id":-9007199254740993,"method":"ping"}""", "-9007199254740993")]
    public void Id_is_kept_exactly_as_the_client_wrote_it(string json, string expectedId) =>
        Assert.Equal(expectedId, Single(json).Id);

    [Theory]
    [InlineData("""{"method":"notifications/initialized"}""")]
    [InlineData("""{"id":null,"method":"notifications/initialized"}""")]
    public void Notification_has_no_id(string json)
    {
        var call = Single(json);

        Assert.Null(call.Id);
        Assert.Equal("notifications/initialized", call.Method);
    }

    [Fact]
    public void Response_sent_by_the_client_has_no_method()
    {
        var call = Single("""{"jsonrpc":"2.0","id":3,"result":{"content":[]}}""");

        Assert.Equal(new RpcCall("3", null, null), call);
        Assert.False(call.IsToolCall);
    }

    [Fact]
    public void Name_in_params_is_read_only_for_tool_calls()
    {
        Assert.Null(Single("""{"id":1,"method":"prompts/get","params":{"name":"summary"}}""").Tool);
        Assert.Null(Single("""{"id":1,"method":"tools/call","params":{"name":42}}""").Tool);
        Assert.Null(Single("""{"id":1,"method":"tools/call","params":["echo"]}""").Tool);
        Assert.Null(Single("""{"id":1,"method":"tools/call"}""").Tool);
    }

    [Fact]
    public void Order_of_properties_and_nested_look_alikes_do_not_matter()
    {
        var call = Single(
            """{"params":{"arguments":{"name":"inner","method":"ping","id":9},"name":"echo"},"extra":{"method":"ping"},"method":"tools/call","id":1}""");

        Assert.Equal(new RpcCall("1", "tools/call", "echo"), call);
    }

    [Fact]
    public void Escaped_spelling_of_a_tool_call_is_still_a_tool_call()
    {
        var call = Single("""{"id":1,"method":"tools\/call","params":{"name":"echo"}}""");

        Assert.Equal(new RpcCall("1", "tools/call", "echo"), call);
    }

    [Fact]
    public void Batch_lists_every_message_in_order()
    {
        var envelope = Read(
            """[{"id":1,"method":"tools/call","params":{"name":"a"}},{"method":"notifications/progress"},{"id":"2","method":"tools/call","params":{"name":"b"}}]""");

        Assert.NotNull(envelope);
        Assert.True(envelope.IsBatch);
        Assert.Equal(
            [new RpcCall("1", "tools/call", "a"), new RpcCall(null, "notifications/progress", null), new RpcCall("\"2\"", "tools/call", "b")],
            envelope.Calls);
        Assert.Equal(2, envelope.ToolCalls);
    }

    [Fact]
    public void Batch_size_is_capped()
    {
        string Batch(int size) => "[" + string.Join(",", Enumerable.Repeat("""{"id":1,"method":"tools/call"}""", size)) + "]";

        Assert.Equal(RpcEnvelope.MaxBatch, Read(Batch(RpcEnvelope.MaxBatch))!.ToolCalls);
        Assert.Null(Read(Batch(RpcEnvelope.MaxBatch + 1)));
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("not json")]
    [InlineData("42")]
    [InlineData("\"tools/call\"")]
    [InlineData("null")]
    [InlineData("[]")]
    [InlineData("[1]")]
    [InlineData("""[{"id":1,"method":"ping"},[]]""")]
    [InlineData("""{"id":1,"method":"ping"}{"id":2,"method":"tools/call"}""")]
    [InlineData("""{"id":1,"method":"ping"} trailing""")]
    [InlineData("{\"id\":1,\"method\":\"ping\\\"}")]
    [InlineData("""{"id":1,"method":42}""")]
    [InlineData("""{"id":1,"method":null}""")]
    [InlineData("""{"id":{"a":1},"method":"ping"}""")]
    [InlineData("""{"id":[1],"method":"ping"}""")]
    [InlineData("""{"id":true,"method":"ping"}""")]
    public void Anything_that_is_not_a_json_rpc_message_is_rejected(string json) => Assert.Null(Read(json));

    [Theory]
    [InlineData("""{"id":1,"method":"ping","method":"tools/call"}""")]
    [InlineData("""{"id":1,"method":"tools/call","method":"ping"}""")]
    [InlineData("""{"id":1,"id":2,"method":"tools/call"}""")]
    [InlineData("""{"id":1,"method":"tools/call","params":{},"params":{"name":"echo"}}""")]
    [InlineData("""{"id":1,"method":"tools/call","params":{"name":"a","name":"b"}}""")]
    public void Message_that_two_parsers_could_read_differently_is_rejected(string json) => Assert.Null(Read(json));

    [Fact]
    public void Deeply_nested_arguments_are_read_and_absurd_nesting_is_rejected()
    {
        string Nested(int depth) =>
            """{"id":1,"method":"tools/call","params":{"name":"echo","arguments":""" + new string('[', depth) + new string(']', depth) + "}}";

        Assert.Equal("echo", Single(Nested(200)).Tool);
        Assert.Null(Read(Nested(300)));
    }

    [Fact]
    public void Long_or_unprintable_names_are_shortened_and_cleaned_before_anyone_stores_them()
    {
        var longName = new string('x', 5_000);
        var longTool = Single($$$"""{"id":1,"method":"tools/call","params":{"name":"{{{longName}}}"}}""");
        var dirtyTool = Single("""{"id":1,"method":"tools/call","params":{"name":"a\u0000b\nc\u001b[31m"}}""");
        var longMethod = Single($$"""{"id":1,"method":"tools/call{{longName}}"}""");
        var dirtyMethod = Single("""{"id":1,"method":"tools/call\u0000"}""");

        Assert.Equal(new string('x', RpcEnvelope.MaxNameLength), longTool.Tool);
        Assert.Equal("a?b?c?[31m", dirtyTool.Tool);
        Assert.Equal(RpcEnvelope.MaxNameLength, longMethod.Method!.Length);
        Assert.False(longMethod.IsToolCall);
        Assert.Equal("tools/call?", dirtyMethod.Method);
        Assert.False(dirtyMethod.IsToolCall);
    }
}
