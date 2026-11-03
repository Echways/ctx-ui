using Microsoft.AspNetCore.DataProtection;

namespace CtxUi.Builder.Servers;

public sealed class SecretBox(IDataProtectionProvider provider)
{
    private const string Purpose = "CtxUi.Builder.Secrets";

    private readonly IDataProtector _protector = provider.CreateProtector(Purpose);

    public string Seal(string value) => _protector.Protect(value);

    public string Open(string sealedValue) => _protector.Unprotect(sealedValue);
}
