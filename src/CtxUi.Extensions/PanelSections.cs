using System.Reflection;
using Microsoft.AspNetCore.Components;

namespace CtxUi.Extensions;

public static class PanelSections
{
    public const string Overview = "overview";
    public const string Metrics = "metrics";
    public const string Inbounds = "inbounds";
    public const string Clients = "clients";
    public const string Journal = "journal";
    public const string AccessCaption = "access";
    public const string OAuth = "oauth";
    public const string ApiKeys = "keys";

    public static string Of<TPage>() =>
        typeof(TPage).GetCustomAttributes<RouteAttribute>().FirstOrDefault()?.Template.TrimStart('/')
        ?? throw new InvalidOperationException($"{typeof(TPage).FullName} has no @page route to name a panel section by");
}
