using CtxUi.Builder.Tools;

namespace CtxUi.Builder.Tests;

public sealed class CodeToolNamesTests
{
    [Fact]
    public void Decorated_functions_and_explicit_names_are_found_in_tool_files_only()
    {
        var names = CodeToolNames.From(
        [
            ("mcp/a.py",
                "from fastmcp.tools import tool\n\n\n@tool\ndef alpha(x: int):\n    return x\n\n\n@tool(name=\"beta-name\")\nasync def beta_impl():\n    pass\n\n\n@tool(description='d')\ndef gamma():\n    pass\n\n\ndef helper():\n    pass\n"),
            ("mcp/data.json", "@tool\ndef ignored(): pass"),
            ("other/b.py", "@tool\ndef elsewhere(): pass")
        ]);

        Assert.Equal(["alpha", "beta-name", "gamma"], names.Order(StringComparer.Ordinal));
    }

    [Fact]
    public void The_file_the_panel_generates_is_recognised()
    {
        var (_, python) = OpenApiDraft.ConvertToCode(
            OpenApiDraft.Upsert(string.Empty, new FormTool("1forecast", "GET", "/f", "d", [], null)), "1forecast");

        Assert.Equal(["1forecast"], CodeToolNames.From([("mcp/tool_1forecast.py", python)]));
    }

    [Theory]
    [InlineData("@tool(description=\"Search (fuzzy)\")\ndef search():\n    pass\n", "search")]
    [InlineData("@tool  # a comment\ndef commented():\n    pass\n", "commented")]
    [InlineData("@tool\n\n\ndef spaced():\n    pass\n", "spaced")]
    [InlineData("@tool(annotations=dict(readOnlyHint=True), name=\"annotated\")\ndef fn():\n    pass\n", "annotated")]
    [InlineData("from fastmcp.tools import tool as t\n\n@t(name=\"aliased\")\ndef fn():\n    pass\n", "aliased")]
    [InlineData("from fastmcp.tools import tool as t\n\n@t\nasync def aliased_plain():\n    pass\n", "aliased_plain")]
    [InlineData("import fastmcp\n\n@fastmcp.tools.tool\ndef dotted():\n    pass\n", "dotted")]
    [InlineData("from fastmcp import tools\n\n@tools.tool(\"positional\")\ndef fn():\n    pass\n", "positional")]
    [InlineData("@tool(description=\"say name=\\\"fake\\\" here\")\ndef real():\n    pass\n", "real")]
    [InlineData("@tool(\n    description=\"multi\",\n    name='lines',\n)\n@other\ndef fn():\n    pass\n", "lines")]
    public void Decorator_forms_are_recognised(string source, string expected)
    {
        Assert.Equal([expected], CodeToolNames.From([("mcp/a.py", source)]));
    }
}
