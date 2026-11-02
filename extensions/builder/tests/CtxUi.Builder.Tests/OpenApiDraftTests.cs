using System.Text.Json.Nodes;
using CtxUi.Builder.Tools;
using Enfilade;

namespace CtxUi.Builder.Tests;

public sealed class OpenApiDraftTests
{
    private static FormTool Forecast() => new(
        "get_forecast",
        "GET",
        "/v1/forecast/{city}",
        "Weather forecast for N days",
        [
            new ToolParameter("city", "string", ParameterPlace.Path, true, null, "City name"),
            new ToolParameter("days", "integer", ParameterPlace.Query, false, JsonValue.Create(3), null),
            new ToolParameter("X-Trace", "string", ParameterPlace.Header, false, null, null),
        ],
        JsonNode.Parse("""{"type":"object","properties":{"temp":{"type":"number"}}}"""));

    private static void AssertSameTool(FormTool expected, FormTool actual)
    {
        Assert.Equal((expected.Name, expected.Method, expected.Path, expected.Description), (actual.Name, actual.Method, actual.Path, actual.Description));
        Assert.Equal(expected.Parameters.Count, actual.Parameters.Count);
        foreach (var (left, right) in expected.Parameters.Zip(actual.Parameters))
        {
            Assert.Equal((left.Name, left.Type, left.Place, left.Required, left.Description), (right.Name, right.Type, right.Place, right.Required, right.Description));
            Assert.Equal(left.Default?.ToJsonString(), right.Default?.ToJsonString());
        }

        Assert.Equal(expected.ResponseSchema?.ToJsonString(), actual.ResponseSchema?.ToJsonString());
    }

    [Fact]
    public void Upsert_then_read_returns_the_same_tool()
    {
        var tool = Forecast();

        var read = Assert.Single(OpenApiDraft.Read(OpenApiDraft.Upsert(string.Empty, tool)));

        AssertSameTool(tool, read);
    }

    [Fact]
    public void Body_parameters_round_trip_through_the_request_body()
    {
        var tool = new FormTool("send_alert", "POST", "/alerts", "Send an alert",
        [
            new ToolParameter("text", "string", ParameterPlace.Body, true, null, "Message"),
            new ToolParameter("urgent", "boolean", ParameterPlace.Body, false, JsonValue.Create(false), null),
        ], null);

        var json = OpenApiDraft.Upsert(string.Empty, tool);

        AssertSameTool(tool, Assert.Single(OpenApiDraft.Read(json)));
        var body = JsonNode.Parse(json)!["paths"]!["/alerts"]!["post"]!["requestBody"]!["content"]!["application/json"]!["schema"]!;
        Assert.Equal("text", body["required"]![0]!.GetValue<string>());
    }

    [Fact]
    public void Path_parameter_must_appear_in_the_path()
    {
        var missing = Forecast() with { Path = "/v1/forecast" };
        var undeclared = Forecast() with { Parameters = [] };

        Assert.Throws<PanelException>(() => OpenApiDraft.Upsert(string.Empty, missing));
        Assert.Throws<PanelException>(() => OpenApiDraft.Upsert(string.Empty, undeclared));
    }

    [Fact]
    public void Tool_name_is_kept_as_operation_id()
    {
        var json = JsonNode.Parse(OpenApiDraft.Upsert(string.Empty, Forecast()))!;

        Assert.Equal("get_forecast", json["paths"]!["/v1/forecast/{city}"]!["get"]!["operationId"]!.GetValue<string>());
        Assert.Throws<PanelException>(() => OpenApiDraft.Upsert(string.Empty, Forecast() with { Name = "get forecast" }));
    }

    [Fact]
    public void Upsert_keeps_fields_outside_the_form()
    {
        const string existing = """
        {
          "openapi": "3.0.3",
          "info": { "title": "Weather", "version": "2" },
          "x-owner": "ops",
          "paths": {
            "/v1/forecast/{city}": {
              "x-path": 1,
              "get": {
                "operationId": "get_forecast",
                "tags": ["weather"],
                "x-rate": 5,
                "parameters": [
                  { "name": "city", "in": "path", "required": true, "schema": { "type": "string", "enum": ["Berlin"] } },
                  { "name": "session", "in": "cookie", "schema": { "type": "string" } }
                ],
                "responses": { "200": { "description": "Fine" }, "404": { "description": "Missing" } }
              }
            }
          }
        }
        """;

        var json = JsonNode.Parse(OpenApiDraft.Upsert(existing, Forecast()))!;

        Assert.Equal("ops", json["x-owner"]!.GetValue<string>());
        var path = json["paths"]!["/v1/forecast/{city}"]!;
        Assert.Equal(1, path["x-path"]!.GetValue<int>());
        var operation = path["get"]!;
        Assert.Equal("weather", operation["tags"]![0]!.GetValue<string>());
        Assert.Equal(5, operation["x-rate"]!.GetValue<int>());
        Assert.Equal("Berlin", operation["parameters"]![0]!["schema"]!["enum"]![0]!.GetValue<string>());
        Assert.Contains(operation["parameters"]!.AsArray(), node => node!["in"]!.GetValue<string>() == "cookie");
        Assert.Equal("Missing", operation["responses"]!["404"]!["description"]!.GetValue<string>());
        Assert.Equal("Fine", operation["responses"]!["200"]!["description"]!.GetValue<string>());
    }

    [Fact]
    public void Upsert_moves_a_tool_to_a_new_route()
    {
        var first = OpenApiDraft.Upsert(string.Empty, Forecast());

        var moved = OpenApiDraft.Upsert(first, Forecast() with { Method = "POST", Path = "/v2/forecast/{city}" });

        var tool = Assert.Single(OpenApiDraft.Read(moved));
        Assert.Equal(("POST", "/v2/forecast/{city}"), (tool.Method, tool.Path));
        Assert.Null(JsonNode.Parse(moved)!["paths"]!["/v1/forecast/{city}"]);
    }

    [Fact]
    public void Upsert_refuses_a_route_that_belongs_to_another_tool()
    {
        var first = OpenApiDraft.Upsert(string.Empty, Forecast());

        Assert.Throws<PanelException>(() => OpenApiDraft.Upsert(first, Forecast() with { Name = "other" }));
    }

    [Fact]
    public void Remove_deletes_the_operation_and_its_empty_path()
    {
        var json = OpenApiDraft.Upsert(string.Empty, Forecast());

        var removed = OpenApiDraft.Remove(json, "get_forecast");

        Assert.Empty(OpenApiDraft.Read(removed));
        Assert.Empty(JsonNode.Parse(removed)!["paths"]!.AsObject());
        Assert.Throws<PanelException>(() => OpenApiDraft.Remove(removed, "get_forecast"));
    }

    [Fact]
    public void Convert_to_code_removes_the_operation_and_produces_a_tool_file()
    {
        var json = OpenApiDraft.Upsert(string.Empty, Forecast());

        var (remaining, python) = OpenApiDraft.ConvertToCode(json, "get_forecast");

        Assert.Empty(OpenApiDraft.Read(remaining));
        Assert.Contains("@tool", python);
        Assert.Contains("async def get_forecast(city: str, days: int = 3, x_trace: str | None = None):", python);
        Assert.Contains("\"GET\"", python);
        Assert.Contains("BUILDER_BASE_URL", python);
        Assert.Contains("Weather forecast for N days", python);
        Assert.Equal("mcp/get_forecast.py", OpenApiDraft.ToolFilePath("get_forecast"));
    }

    [Fact]
    public void Convert_to_code_keeps_the_tool_name_when_it_is_not_a_python_identifier()
    {
        var json = OpenApiDraft.Upsert(string.Empty, Forecast() with { Name = "get-forecast" });

        var (_, python) = OpenApiDraft.ConvertToCode(json, "get-forecast");

        Assert.Contains("@tool(name=\"get-forecast\")", python);
        Assert.Contains("async def get_forecast(", python);
    }

    [Fact]
    public void Convert_to_code_puts_required_parameters_before_optional_ones()
    {
        var tool = new FormTool("send", "POST", "/send",  null,
        [
            new ToolParameter("note", "string", ParameterPlace.Body, false, null, null),
            new ToolParameter("text", "string", ParameterPlace.Body, true, null, null),
        ], null);

        var (_, python) = OpenApiDraft.ConvertToCode(OpenApiDraft.Upsert(string.Empty, tool), "send");

        Assert.Contains("async def send(text: str, note: str | None = None):", python);
    }

    [Fact]
    public void Broken_document_is_refused_with_a_message()
    {
        var failure = Assert.Throws<PanelException>(() => OpenApiDraft.Read("{ not json"));

        Assert.Equal(BuilderText.OpenApiInvalid, failure.Message);
    }

    [Fact]
    public void Operation_without_operation_id_is_read_under_a_generated_name()
    {
        const string json = """{"openapi":"3.0.3","paths":{"/pets/{id}":{"get":{"responses":{"200":{"description":"OK"}}}}}}""";

        Assert.Equal("get_pets_id", Assert.Single(OpenApiDraft.Read(json)).Name);
    }
}
