using System.Reflection;
using System.Text.RegularExpressions;
using Microsoft.AspNetCore.Components;
using CtxUi.Connect;
using CtxUi.Extensions;
using CtxUi.Gateway;
using CtxUi.Panel;

namespace CtxUi.Server.Extensions;

public static partial class ExtensionRules
{
    private static readonly Lazy<HashSet<string>> Reserved = new(ReservedSegments);

    public static bool IsValidId(string id) => ValidId().IsMatch(id);

    public static bool IsCompatible(string api) =>
        Version.TryParse(api, out var version)
        && version.Major == ExtensionApi.Version.Major
        && version.Minor <= ExtensionApi.Version.Minor;

    public static bool IsReservedId(string id) => Reserved.Value.Contains(id);

    public static string? RouteViolation(string id, Assembly assembly) => RouteViolation(id, RoutesOf(assembly));

    public static string? RouteViolation(string id, IEnumerable<string> routes)
    {
        var own = "/" + id;
        var outside = routes.FirstOrDefault(route =>
        {
            var path = "/" + route.TrimStart('/');
            return !path.Equals(own, StringComparison.OrdinalIgnoreCase)
                && !path.StartsWith(own + "/", StringComparison.OrdinalIgnoreCase);
        });
        return outside is null ? null : string.Format(ServerText.ExtensionRouteOutside, outside, own);
    }

    private static IEnumerable<string> RoutesOf(Assembly assembly) =>
        assembly.GetTypes()
            .Where(type => typeof(IComponent).IsAssignableFrom(type))
            .SelectMany(type => type.GetCustomAttributes<RouteAttribute>())
            .Select(route => route.Template);

    private static HashSet<string> ReservedSegments()
    {
        string[] server = ["/api", "/extensions", "/_content", "/_framework", "/_blazor", GatewayServices.Path, HealthProbe.Path];
        var open = ConnectServices.OpenPaths.Select(path => path.Value!);
        return [.. server.Concat(open).Concat(RoutesOf(typeof(PanelServices).Assembly)).Select(FirstSegment).Where(segment => segment.Length > 0)];
    }

    private static string FirstSegment(string route) => route.TrimStart('/').Split('/')[0].ToLowerInvariant();

    [GeneratedRegex(@"^[a-z][a-z0-9-]{0,31}\z")]
    private static partial Regex ValidId();
}
