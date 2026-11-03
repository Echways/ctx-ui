using CtxUi.Server.Extensions;

namespace CtxUi.Server.Tests;

public class ExtensionRulesTests
{
    [Theory]
    [InlineData("sample")]
    [InlineData("my-ext2")]
    [InlineData("a23456789012345678901234567890ab")]
    public void A_lowercase_id_up_to_32_characters_is_valid(string id) => Assert.True(ExtensionRules.IsValidId(id));

    [Theory]
    [InlineData("")]
    [InlineData("Sample")]
    [InlineData("../x")]
    [InlineData("-a")]
    [InlineData("sample\n")]
    [InlineData("a234567890123456789012345678901ab")]
    public void Any_other_id_is_not(string id) => Assert.False(ExtensionRules.IsValidId(id));

    [Fact]
    public void A_contract_is_compatible_only_at_the_host_major_and_up_to_its_minor()
    {
        Assert.True(ExtensionRules.IsCompatible("1.0"));
        Assert.True(ExtensionRules.IsCompatible("1.1"));
        Assert.False(ExtensionRules.IsCompatible("1.2"));
        Assert.False(ExtensionRules.IsCompatible("2.0"));
        Assert.False(ExtensionRules.IsCompatible("0.9"));
        Assert.False(ExtensionRules.IsCompatible("abc"));
    }

    [Fact]
    public void A_manifest_ignores_fields_it_does_not_know()
    {
        var folder = Directory.CreateTempSubdirectory("ctx-ui-manifest-").FullName;
        File.WriteAllText(
            Path.Combine(folder, ExtensionManifest.FileName),
            """{ "id": "sample", "name": "Sample", "version": "1.2.3", "api": "1.0", "assembly": "SampleExtension", "publisher": "Acme" }""");

        var manifest = ExtensionManifest.Read(folder);

        Assert.Equal(new ExtensionManifest("sample", "Sample", "1.2.3", "1.0", "SampleExtension"), manifest);
        Directory.Delete(folder, recursive: true);
    }

    [Theory]
    [InlineData("""{ "id": "sample" }""")]
    [InlineData("""{ "id": "sample", "name": "Sample", "version": "1", "api": "1.0", "assembly": "../../evil" }""")]
    [InlineData("not json")]
    public void A_manifest_with_missing_or_unsafe_fields_is_refused(string text)
    {
        var folder = Directory.CreateTempSubdirectory("ctx-ui-manifest-").FullName;
        File.WriteAllText(Path.Combine(folder, ExtensionManifest.FileName), text);

        Assert.ThrowsAny<Exception>(() => ExtensionManifest.Read(folder));
        Directory.Delete(folder, recursive: true);
    }

    [Theory]
    [InlineData("login")]
    [InlineData("clients")]
    [InlineData("inbounds")]
    [InlineData("api")]
    [InlineData("extensions")]
    [InlineData("_content")]
    [InlineData("_framework")]
    [InlineData("mcp")]
    [InlineData("healthz")]
    [InlineData("connect")]
    public void Paths_the_server_uses_are_reserved(string id) => Assert.True(ExtensionRules.IsReservedId(id));

    [Fact]
    public void Other_ids_are_free() => Assert.False(ExtensionRules.IsReservedId("sample"));

    [Fact]
    public void Routes_under_the_id_are_fine()
    {
        Assert.Null(ExtensionRules.RouteViolation("sample", ["/sample", "/sample/boom", "/sample/{id}", "sample/x"]));
    }

    [Theory]
    [InlineData("/login")]
    [InlineData("/samplex")]
    [InlineData("/")]
    [InlineData("/{*rest}")]
    public void A_route_outside_the_id_is_a_violation_naming_it(string route)
    {
        Assert.Contains(route, ExtensionRules.RouteViolation("sample", ["/sample", route]));
    }

    [Fact]
    public void The_routes_of_a_loaded_assembly_are_checked()
    {
        var directory = Path.Combine(AppContext.BaseDirectory, "extensions", "sample");
        var assembly = new ExtensionLoadContext("sample", Path.Combine(directory, "SampleExtension.dll"))
            .LoadFromAssemblyPath(Path.Combine(directory, "SampleExtension.dll"));

        Assert.Null(ExtensionRules.RouteViolation("sample", assembly));
        Assert.Contains("/sample", ExtensionRules.RouteViolation("other", assembly));
    }
}
