using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
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
        app.MapPost(ConnectResults.SignInPath, SignInWithKeyAsync)
            .RequireSetting(signIn => signIn.KeySignIn)
            .RequirePageForm()
            .RequireRateLimiting(SignInPolicy);
        app.MapGet(ConnectResults.DevicesPath, DevicesAsync);
        app.MapPost(ConnectResults.DevicesPath + "/{id:guid}/revoke", RevokeAsync).RequireSetting(signIn => signIn.SelfRevoke).RequirePageForm();
        app.MapPost(ConnectServices.Prefix + "/signout", (Delegate)SignOutAsync).RequirePageForm();
        app.MapGet(ConnectServices.Prefix + "/language/{culture}", (HttpContext http, string culture, string? returnUrl) =>
            ConnectLanguage.Choose(http, culture, returnUrl));
        return app;
    }

    private static async Task<IResult> SignInPageAsync(HttpContext http, ISnapshotSource snapshot, ConnectTargets targets, string? returnUrl)
    {
        var target = ConnectLanguage.LocalPath(returnUrl, ConnectResults.DevicesPath);
        return SignInPage(snapshot, target, await targets.FromReturnUrlAsync(target, http.RequestAborted));
    }

    private static async Task<IResult> SignInWithKeyAsync(
        HttpContext http,
        [FromForm] string? key,
        [FromForm] string? returnUrl,
        ISnapshotSource snapshot,
        ConnectTargets targets,
        TimeProvider clock)
    {
        var target = ConnectLanguage.LocalPath(returnUrl, ConnectResults.DevicesPath);
        var found = KeySignIn.Find(snapshot.Current, key, clock.GetUtcNow());
        if (found.ClientId is not { } clientId)
            return SignInPage(snapshot, target, await targets.FromReturnUrlAsync(target, http.RequestAborted), found.Refusal);

        await ConnectSession.SignInAsync(http, clientId, Device.ByKey);
        return ConnectResults.Back(http, target);
    }

    private static IResult SignInPage(ISnapshotSource snapshot, string returnUrl, ConnectTarget? target, string? error = null) =>
        ConnectResults.Page<SignInPage>(new
        {
            ReturnUrl = returnUrl,
            snapshot.Current.SignIn.KeySignIn,
            Providers = ProviderButton.All(snapshot.Current.SignIn),
            Target = target,
            Error = error,
        });

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
        var current = listed.FirstOrDefault(device => device.MarkerHash == markerHash);
        return ConnectResults.Page<DevicesPage>(new
        {
            Devices = current is null ? listed : listed.Where(device => device != current).Prepend(current).ToList(),
            ClientName = signedIn.Client.Name,
            CurrentDeviceId = current?.Id,
            snapshot.Current.SignIn.SelfRevoke,
        });
    }

    private static async Task<IResult> RevokeAsync(
        HttpContext http,
        Guid id,
        ISnapshotSource snapshot,
        DeviceService devices)
    {
        if (await SignedIn.ReadAsync(http, snapshot) is not { } signedIn)
            return ConnectResults.ToSignIn(http, ConnectResults.DevicesPath);

        await devices.RevokeAsync(signedIn.Client.Id, id, DeviceRevocation.User, http.RequestAborted);
        await snapshot.RefreshAsync(http.RequestAborted);
        return ConnectResults.Back(http, ConnectResults.DevicesPath);
    }

    private static async Task<IResult> SignOutAsync(HttpContext http)
    {
        await ConnectSession.SignOutAsync(http);
        return ConnectResults.Back(http, ConnectResults.SignInPath);
    }
}
