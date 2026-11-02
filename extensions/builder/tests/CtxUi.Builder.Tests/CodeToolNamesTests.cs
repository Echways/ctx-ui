using CtxUi.Builder.Tools;

namespace CtxUi.Builder.Tests;

public sealed class CodeToolNamesTests
{
    [Fact]
    public void Decorated_functions_and_explicit_names_are_found_in_tool_files_only()
    {
        var names = CodeToolNames.From(
        [
            ("mcp/a.py", "from fastmcp.tools import tool\n\n\n@tool\ndef alpha(x: int):\n    return x\n\n\n@tool(name=\"beta-name\")\nasync def beta_impl():\n    pass\n\n\n@tool(description='d')\ndef gamma():\n    pass\n\n\ndef helper():\n    pass\n"),
            ("mcp/data.json", "@tool\ndef ignored(): pass"),
            ("other/b.py", "@tool\ndef elsewhere(): pass"),
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
}
