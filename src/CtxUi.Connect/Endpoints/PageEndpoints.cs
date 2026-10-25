using Microsoft.AspNetCore.Antiforgery;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.RateLimiting;
using Microsoft.AspNetCore.Routing;
using CtxUi.Connect.Pages;
using CtxUi.Connect.SignIn;
using CtxUi.Core.Routing;
using CtxUi.Core.SignIn;

namespace CtxUi.Connect.Endpoints;

public static class PageEndpoints
{
    public const string SignInPolicy = "connect-signin";

    public static IEndpointRouteBuilder MapPages(this IEndpointRouteBuilder app)
    {
        app.MapGet(ConnectResults.SignInPath, SignInPageAsync);
        app.MapPost(ConnectResults.SignInPath, SignInWithKeyAsync).DisableAntiforgery().RequireRateLimiting(SignInPolicy);
        app.MapGet(ConnectResults.DevicesPath, DevicesAsync);
        app.MapPost(ConnectResults.DevicesPath + "/{id:guid}/revoke", RevokeAsync);
        app.MapPost(ConnectServices.Prefix + "/signout", (Delegate)SignOutAsync);
        app.MapGet(ConnectServices.Prefix + "/language/{culture}", (HttpContext http, string culture, string? returnUrl) =>
            ConnectLanguage.Choose(http, culture, returnUrl));
        return app;
    }

    private static IResult SignInPageAsync(HttpContext http, ISnapshotSource snapshot, string? returnUrl) =>
        ConnectResults.Page<SignInPage>(new
        {
            ReturnUrl = ConnectLanguage.LocalPath(returnUrl, ConnectResults.DevicesPath),
            snapshot.Current.SignIn.KeySignIn,
        });

    private static async Task<IResult> SignInWithKeyAsync(
        HttpContext http,
        [FromForm] string? key,
        [FromForm] string? returnUrl,
        ISnapshotSource snapshot,
        IAntiforgery antiforgery,
        TimeProvider clock)
    {
        var target = ConnectLanguage.LocalPath(returnUrl, ConnectResults.DevicesPath);
        if (!snapshot.Current.SignIn.KeySignIn)
            return Results.NotFound();
        if (!await antiforgery.IsRequestValidAsync(http))
            return Results.BadRequest();

        var found = KeySignIn.Find(snapshot.Current, key, clock.GetUtcNow());
        if (found.ClientId is not { } clientId)
            return ConnectResults.Page<SignInPage>(new { ReturnUrl = target, KeySignIn = true, Error = found.Refusal });

        await ConnectSession.SignInAsync(http, clientId, Device.ByKey);
        return ConnectResults.Back(http, target);
    }

    private static async Task<IResult> DevicesAsync(
        HttpContext http,
        ISnapshotSource snapshot,
        DeviceService devices,
        TimeProvider clock)
    {
        if (await SignedIn.ReadAsync(http, snapshot) is not { } signedIn)
            return ConnectResults.ToSignIn(http, ConnectResults.DevicesPath);

        var listed = await devices.ListAsync(signedIn.Client.Id, clock.GetUtcNow(), http.RequestAborted);
        var markerHash = DeviceMarker.Hash(DeviceMarkerCookie.GetOrIssue(http));
        return ConnectResults.Page<DevicesPage>(new
        {
            Devices = listed,
            ClientName = signedIn.Client.Name,
            CurrentDeviceId = listed.FirstOrDefault(device => device.MarkerHash == markerHash)?.Id,
            snapshot.Current.SignIn.SelfRevoke,
        });
    }

    private static async Task<IResult> RevokeAsync(
        HttpContext http,
        Guid id,
        ISnapshotSource snapshot,
        DeviceService devices)
    {
        if (!snapshot.Current.SignIn.SelfRevoke)
            return Results.NotFound();
        if (await SignedIn.ReadAsync(http, snapshot) is not { } signedIn)
            return ConnectResults.ToSignIn(http, ConnectResults.DevicesPath);

        await devices.RevokeAsync(signedIn.Client.Id, id, DeviceRevocation.User, http.RequestAborted);
        await snapshot.RefreshAsync();
        return ConnectResults.Back(http, ConnectResults.DevicesPath);
    }

    private static async Task<IResult> SignOutAsync(HttpContext http)
    {
        await ConnectSession.SignOutAsync(http);
        return ConnectResults.Back(http, ConnectResults.SignInPath);
    }
}
