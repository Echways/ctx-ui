using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Localization;

namespace CtxUi.Connect.SignIn;

public sealed class BrowserLanguage : AcceptLanguageHeaderRequestCultureProvider
{
    public override Task<ProviderCultureResult?> DetermineProviderCultureResult(HttpContext httpContext) =>
        httpContext.Request.Path.StartsWithSegments(ConnectServices.Prefix)
            ? base.DetermineProviderCultureResult(httpContext)
            : NullProviderCultureResult;
}
