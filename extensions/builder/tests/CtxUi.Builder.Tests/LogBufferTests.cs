using CtxUi.Builder.Publishing;

namespace CtxUi.Builder.Tests;

public sealed class LogBufferTests
{
    [Fact]
    public void Chunks_are_joined_into_lines_across_boundaries()
    {
        var buffer = new LogBuffer();

        buffer.Append("first li");
        buffer.Append("ne\nsecond\nthi");

        Assert.Equal(["first line", "second", "thi"], buffer.Lines);
        buffer.Append("rd\n");
        Assert.Equal(["first line", "second", "third"], buffer.Lines);
    }

    [Fact]
    public void Only_the_last_two_thousand_lines_are_kept()
    {
        var buffer = new LogBuffer();

        buffer.Append(string.Concat(Enumerable.Range(1, 2500).Select(number => $"line {number}\n")));

        Assert.Equal(LogBuffer.MaxLines, buffer.Lines.Count);
        Assert.Equal("line 501", buffer.Lines[0]);
        Assert.Equal("line 2500", buffer.Lines[^1]);
    }

    [Fact]
    public void Carriage_returns_are_dropped()
    {
        var buffer = new LogBuffer();

        buffer.Append("a\r\nb\r\n");

        Assert.Equal(["a", "b"], buffer.Lines);
    }

    [Fact]
    public void A_very_long_line_without_a_break_is_cut()
    {
        var buffer = new LogBuffer();

        buffer.Append(new string('x', LogBuffer.MaxLineLength * 3));

        Assert.All(buffer.Lines, line => Assert.True(line.Length <= LogBuffer.MaxLineLength));
    }
}
