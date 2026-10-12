using System.Security.Cryptography;
using Microsoft.AspNetCore.DataProtection;
using CtxUi.Core.Secrets;

namespace CtxUi.Server;

public sealed class DataProtectionVault(IDataProtectionProvider provider) : ISecretVault
{
    private readonly IDataProtector _protector = provider.CreateProtector("CtxUi.Secrets.v1");

    public string Seal(string plaintext) => _protector.Protect(plaintext);

    public string? Open(string sealedText)
    {
        try
        {
            return _protector.Unprotect(sealedText);
        }
        catch (CryptographicException)
        {
            return null;
        }
        catch (FormatException)
        {
            return null;
        }
    }
}
