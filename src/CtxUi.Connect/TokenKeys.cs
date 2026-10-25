using System.Security.Cryptography;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.IdentityModel.Tokens;

namespace CtxUi.Connect;

public static class TokenKeys
{
    public const string SigningFile = "oauth-signing.key";
    public const string EncryptionFile = "oauth-encryption.key";

    private const string Purpose = "CtxUi.OAuth.Keys";
    private const int KeySize = 2048;
    private const UnixFileMode OwnerOnly = UnixFileMode.UserRead | UnixFileMode.UserWrite;

    public static (RsaSecurityKey Signing, RsaSecurityKey Encryption) LoadOrCreate(string keysDir, IDataProtectionProvider protection)
    {
        var protector = protection.CreateProtector(Purpose);
        return (LoadOrCreate(Path.Combine(keysDir, SigningFile), protector), LoadOrCreate(Path.Combine(keysDir, EncryptionFile), protector));
    }

    private static RsaSecurityKey LoadOrCreate(string path, IDataProtector protector)
    {
        var rsa = TryLoad(path, protector) ?? Create(path, protector);
        var key = new RsaSecurityKey(rsa);
        key.KeyId = Base64UrlEncoder.Encode(key.ComputeJwkThumbprint());
        return key;
    }

    private static RSA? TryLoad(string path, IDataProtector protector)
    {
        if (!File.Exists(path))
            return null;

        var rsa = RSA.Create();
        try
        {
            rsa.ImportRSAPrivateKey(protector.Unprotect(File.ReadAllBytes(path)), out _);
            return rsa;
        }
        catch (CryptographicException)
        {
            rsa.Dispose();
            return null;
        }
    }

    private static RSA Create(string path, IDataProtector protector)
    {
        var rsa = RSA.Create(KeySize);
        File.Delete(path);
        var file = new FileStreamOptions { Mode = FileMode.CreateNew, Access = FileAccess.Write };
        if (!OperatingSystem.IsWindows())
            file.UnixCreateMode = OwnerOnly;

        using (var stream = new FileStream(path, file))
            stream.Write(protector.Protect(rsa.ExportRSAPrivateKey()));

        return rsa;
    }
}
