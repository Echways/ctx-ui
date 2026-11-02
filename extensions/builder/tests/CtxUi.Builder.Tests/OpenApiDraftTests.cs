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
        foreach (var invalid in new[] { "get forecast", "get-forecast", "a__b", "_a", "a_", new string('x', 57) })
            Assert.Throws<PanelException>(() => OpenApiDraft.Upsert(string.Empty, Forecast() with { Name = invalid }));
        Assert.NotNull(OpenApiDraft.Upsert(string.Empty, Forecast() with { Name = new string('x', 56) }));
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
        var json = OpenApiDraft.Upsert(string.Empty, Forecast() with { Name = "1forecast" });

        var (_, python) = OpenApiDraft.ConvertToCode(json, "1forecast");

        Assert.Contains("@tool(name=\"1forecast\")", python);
        Assert.Contains("async def tool_1forecast(", python);
    }

    [Fact]
    public void Header_values_are_sent_as_strings_with_lower_case_booleans()
    {
        var tool = new FormTool("t", "GET", "/t", null,
        [
            new ToolParameter("X-Count", "integer", ParameterPlace.Header, false, null, null),
            new ToolParameter("X-Debug", "boolean", ParameterPlace.Header, false, null, null),
        ], null);

        var (_, python) = OpenApiDraft.ConvertToCode(OpenApiDraft.Upsert(string.Empty, tool), "t");

        Assert.Contains("(str(value).lower() if isinstance(value, bool) else str(value))", python);
        Assert.Contains("headers = {key: (str(value).lower() if isinstance(value, bool) else str(value)) for key, value in {\"X-Count\": x_count, \"X-Debug\": x_debug}.items() if value is not None}", python);
    }

    [Fact]
    public void Convert_to_code_puts_required_parameters_before_optional_ones()
    {
        var tool = new FormTool("send", "POST", "/send", null,
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

    private const string Imported = """
    {
      "openapi": "3.1.0",
      "info": { "title": "Shop", "version": "1" },
      "paths": {
        "/orders/{id}": {
          "parameters": [{ "name": "id", "in": "path", "required": true, "schema": { "type": "integer" } }],
          "servers": [{ "url": "https://orders.example.com" }],
          "summary": "Order",
          "get": {
            "operationId": "getOrder",
            "parameters": [
              { "name": "expand", "in": "query", "schema": { "anyOf": [{ "type": "string" }, { "type": "null" }] } },
              { "name": "kind", "in": "query", "schema": { "$ref": "#/components/schemas/Kind" } },
              { "name": "ids", "in": "query", "schema": { "type": ["array", "null"], "items": { "type": "string" } } }
            ],
            "responses": { "200": { "description": "OK" } }
          }
        },
        "/orders": {
          "post": {
            "operationId": "createOrder",
            "requestBody": { "required": true, "content": { "application/json": { "schema": { "allOf": [{ "$ref": "#/components/schemas/Base" }, { "type": "object", "properties": { "x": { "type": "string" } } }] } } } },
            "responses": { "201": { "description": "Created" } }
          },
          "put": {
            "operationId": "replaceOrders",
            "requestBody": { "content": { "application/json": { "schema": { "type": "array", "items": { "type": "string" } } } } },
            "responses": { "200": { "description": "OK" } }
          }
        }
      },
      "components": { "schemas": { "Kind": { "type": "string", "enum": ["a", "b"] }, "Base": { "type": "object" } } }
    }
    """;

    private static JsonNode Operation(string json, string path, string method) => JsonNode.Parse(json)!["paths"]![path]![method]!;

    [Fact]
    public void Complex_parameters_are_read_as_complex_and_saved_unchanged()
    {
        var tool = OpenApiDraft.Read(Imported).Single(candidate => candidate.Name == "getOrder");

        Assert.Equal(["id", "expand", "kind", "ids"], tool.Parameters.Select(parameter => parameter.Name));
        Assert.Equal(["integer", "complex", "complex", "complex"], tool.Parameters.Select(parameter => parameter.Type));

        var saved = OpenApiDraft.Upsert(Imported, tool with { Description = "Changed" });

        var parameters = Operation(saved, "/orders/{id}", "get")["parameters"]!.AsArray();
        Assert.Equal(["expand", "kind", "ids"], parameters.Select(node => node!["name"]!.GetValue<string>()));
        Assert.Equal("string", parameters[0]!["schema"]!["anyOf"]![0]!["type"]!.GetValue<string>());
        Assert.Equal("#/components/schemas/Kind", parameters[1]!["schema"]!["$ref"]!.GetValue<string>());
        Assert.Equal("array", parameters[2]!["schema"]!["type"]![0]!.GetValue<string>());
    }

    [Fact]
    public void Path_level_fields_survive_when_a_tool_is_saved_and_when_it_is_removed()
    {
        var tool = OpenApiDraft.Read(Imported).Single(candidate => candidate.Name == "getOrder");

        var saved = JsonNode.Parse(OpenApiDraft.Upsert(Imported, tool))!["paths"]!["/orders/{id}"]!;
        var removed = JsonNode.Parse(OpenApiDraft.Remove(Imported, "getOrder"))!["paths"]!["/orders/{id}"]!;

        Assert.Equal("https://orders.example.com", saved["servers"]![0]!["url"]!.GetValue<string>());
        Assert.Equal("Order", saved["summary"]!.GetValue<string>());
        Assert.Equal("https://orders.example.com", removed["servers"]![0]!["url"]!.GetValue<string>());
        Assert.NotNull(removed["parameters"]);
    }

    [Fact]
    public void Bodies_the_form_cannot_represent_are_kept_as_they_are()
    {
        var tools = OpenApiDraft.Read(Imported);
        var create = tools.Single(candidate => candidate.Name == "createOrder");
        var replace = tools.Single(candidate => candidate.Name == "replaceOrders");

        Assert.DoesNotContain(create.Parameters, parameter => parameter.Place == ParameterPlace.Body);
        Assert.DoesNotContain(replace.Parameters, parameter => parameter.Place == ParameterPlace.Body);

        var saved = OpenApiDraft.Upsert(OpenApiDraft.Upsert(Imported, create with { Description = "Create" }), replace with { Description = "Replace" });

        Assert.NotNull(Operation(saved, "/orders", "post")["requestBody"]!["content"]!["application/json"]!["schema"]!["allOf"]);
        Assert.Equal("array", Operation(saved, "/orders", "put")["requestBody"]!["content"]!["application/json"]!["schema"]!["type"]!.GetValue<string>());
    }

    [Fact]
    public void Adding_body_parameters_to_a_body_the_form_cannot_represent_is_refused()
    {
        var create = OpenApiDraft.Read(Imported).Single(candidate => candidate.Name == "createOrder");
        var extra = create with { Parameters = [new ToolParameter("note", "string", ParameterPlace.Body, false, null, null)] };

        var failure = Assert.Throws<PanelException>(() => OpenApiDraft.Upsert(Imported, extra));

        Assert.Equal(BuilderText.ToolBodyNotEditable, failure.Message);
    }

    [Fact]
    public void A_ref_body_with_properties_stays_editable_and_keeps_its_other_fields()
    {
        const string json = """
        {"openapi":"3.0.3","info":{"title":"t","version":"1"},"paths":{"/a":{"post":{"operationId":"a","requestBody":{"description":"Payload","content":{"application/json":{"schema":{"$ref":"#/components/schemas/Body"}}}},"responses":{"200":{"description":"OK"}}}}},
         "components":{"schemas":{"Body":{"type":"object","title":"Body","properties":{"text":{"type":"string","enum":["x"]},"ref":{"$ref":"#/components/schemas/Body"}}}}}}
        """;

        var tool = Assert.Single(OpenApiDraft.Read(json));
        var saved = OpenApiDraft.Upsert(json, tool);

        Assert.Equal(["string", "complex"], tool.Parameters.Select(parameter => parameter.Type));
        var request = Operation(saved, "/a", "post")["requestBody"]!;
        Assert.Equal("Payload", request["description"]!.GetValue<string>());
        var schema = request["content"]!["application/json"]!["schema"]!;
        Assert.Equal("x", schema["properties"]!["text"]!["enum"]![0]!.GetValue<string>());
        Assert.Equal("#/components/schemas/Body", schema["properties"]!["ref"]!["$ref"]!.GetValue<string>());
        Assert.Equal("Body", schema["title"]!.GetValue<string>());
    }

    [Fact]
    public void Wrong_typed_fields_are_skipped_instead_of_throwing()
    {
        const string json = """
        {"openapi":"3.0.3","paths":{"/a":{"get":{"operationId":5,"description":7,"parameters":[{"name":3,"in":"query"},{"name":"ok","in":"query","required":"yes","description":false,"schema":{"type":5}}],
         "requestBody":{"content":{"application/json":7}},"responses":{"200":{"description":"OK","content":{"application/json":3}}}}},"/b":5,"/c":{"post":{"operationId":"c","requestBody":{"content":{"application/json":{"schema":{"properties":{"p":{"type":[1],"description":2}},"required":[1,"p"]}}}},"responses":"x"}}}}
        """;

        var tools = OpenApiDraft.Read(json);

        Assert.Equal(["get_a", "c"], tools.Select(tool => tool.Name));
        Assert.Equal(["ok"], tools[0].Parameters.Select(parameter => parameter.Name));
        Assert.False(tools[0].Parameters[0].Required);
        Assert.Equal("p", Assert.Single(tools[1].Parameters).Name);
        Assert.NotNull(OpenApiDraft.Upsert(json, tools[0] with { Description = "d" }));
    }

    [Fact]
    public void Complex_parameters_become_untyped_python_arguments()
    {
        var tool = OpenApiDraft.Read(Imported).Single(candidate => candidate.Name == "getOrder");

        var (_, python) = OpenApiDraft.ConvertToCode(Imported, "getOrder");

        Assert.Contains("async def getorder(id: int, expand=None, kind=None, ids=None):", python);
    }

    private const string Inherited = """
    {"openapi":"3.0.3","info":{"title":"t","version":"1"},
     "paths":{"/items/{id}":{"parameters":[{"name":"id","in":"path","required":true,"schema":{"$ref":"#/components/schemas/Id"}},{"name":"v","in":"query","description":"Version","schema":{"type":"integer","format":"int64","minimum":1}}],
       "get":{"operationId":"getItem","responses":{"200":{"description":"OK"}}}}},
     "components":{"schemas":{"Id":{"type":"string"}}}}
    """;

    private static string Effective(string json)
    {
        var path = JsonNode.Parse(json)!["paths"]!["/items/{id}"]!;
        var inherited = path["parameters"]!.AsArray();
        var own = path["get"]!["parameters"]?.AsArray() ?? [];
        return JsonNode.Parse("[" + string.Join(",", inherited.Concat(own).Select(node => node!.ToJsonString())) + "]")!.ToJsonString();
    }

    [Fact]
    public void An_unchanged_save_leaves_inherited_parameters_at_the_path_level()
    {
        var tool = Assert.Single(OpenApiDraft.Read(Inherited));

        var saved = OpenApiDraft.Upsert(Inherited, tool);

        Assert.Null(JsonNode.Parse(saved)!["paths"]!["/items/{id}"]!["get"]!["parameters"]);
        Assert.Equal(Effective(Inherited), Effective(saved));
        Assert.True(JsonNode.DeepEquals(JsonNode.Parse(Inherited), JsonNode.Parse(saved)));
    }

    [Fact]
    public void Changing_an_inherited_parameter_overrides_it_on_top_of_the_path_level_node()
    {
        var tool = Assert.Single(OpenApiDraft.Read(Inherited));
        var changed = tool with { Parameters = [.. tool.Parameters.Select(parameter => parameter.Name == "v" ? parameter with { Description = "Changed" } : parameter)] };

        var saved = OpenApiDraft.Upsert(Inherited, changed);

        var overrides = JsonNode.Parse(saved)!["paths"]!["/items/{id}"]!["get"]!["parameters"]!.AsArray();
        var version = Assert.Single(overrides);
        Assert.Equal("Changed", version!["description"]!.GetValue<string>());
        Assert.Equal("int64", version["schema"]!["format"]!.GetValue<string>());
        Assert.Equal(1, version["schema"]!["minimum"]!.GetValue<int>());
        Assert.Equal("#/components/schemas/Id", JsonNode.Parse(saved)!["paths"]!["/items/{id}"]!["parameters"]![0]!["schema"]!["$ref"]!.GetValue<string>());
    }

    [Fact]
    public void Moving_a_tool_with_inherited_parameters_carries_them_to_the_new_path()
    {
        var tool = Assert.Single(OpenApiDraft.Read(Inherited));

        var moved = OpenApiDraft.Upsert(Inherited, tool with { Path = "/things/{id}" });

        var parameters = JsonNode.Parse(moved)!["paths"]!["/things/{id}"]!["get"]!["parameters"]!.AsArray();
        Assert.Equal(["id", "v"], parameters.Select(node => node!["name"]!.GetValue<string>()));
        Assert.Equal("int64", parameters[1]!["schema"]!["format"]!.GetValue<string>());
        Assert.Equal("#/components/schemas/Id", parameters[0]!["schema"]!["$ref"]!.GetValue<string>());
    }
}
