using System.Text;
using Microsoft.AspNetCore.DataProtection;

namespace CtxUi.Connect.Tests;

public sealed class TokenKeysTests : IDisposable
{
    private readonly DirectoryInfo _keys = Directory.CreateTempSubdirectory("ctx-ui-keys-");
    private readonly IDataProtectionProvider _protection;

    public TokenKeysTests() =>
        _protection = DataProtectionProvider.Create(new DirectoryInfo(Path.Combine(_keys.FullName, "protection")));

    [Fact]
    public void Keys_are_created_once_sealed_and_private()
    {
        var first = TokenKeys.LoadOrCreate(_keys.FullName, _protection);
        var second = TokenKeys.LoadOrCreate(_keys.FullName, _protection);

        Assert.Equal(first.Signing.Rsa!.ExportParameters(false).Modulus, second.Signing.Rsa!.ExportParameters(false).Modulus);
        Assert.Equal(first.Encryption.Rsa!.ExportParameters(false).Modulus, second.Encryption.Rsa!.ExportParameters(false).Modulus);
        Assert.NotEqual(first.Signing.Rsa.ExportParameters(false).Modulus, first.Encryption.Rsa.ExportParameters(false).Modulus);
        Assert.Equal(2048, first.Signing.KeySize);
        foreach (var name in new[] { "oauth-signing.key", "oauth-encryption.key" })
        {
            var path = Path.Combine(_keys.FullName, name);
            Assert.DoesNotContain("PRIVATE KEY", Encoding.UTF8.GetString(File.ReadAllBytes(path)));
            if (!OperatingSystem.IsWindows())
                Assert.Equal(UnixFileMode.UserRead | UnixFileMode.UserWrite, File.GetUnixFileMode(path));
        }
    }

    [Fact]
    public void Unreadable_keys_are_replaced_with_new_ones()
    {
        var old = TokenKeys.LoadOrCreate(_keys.FullName, _protection);
        var stranger = DataProtectionProvider.Create(new DirectoryInfo(Path.Combine(_keys.FullName, "other")));

        var replaced = TokenKeys.LoadOrCreate(_keys.FullName, stranger);

        Assert.NotEqual(old.Signing.Rsa!.ExportParameters(false).Modulus, replaced.Signing.Rsa!.ExportParameters(false).Modulus);
        Assert.Equal(
            replaced.Signing.Rsa.ExportParameters(false).Modulus,
            TokenKeys.LoadOrCreate(_keys.FullName, stranger).Signing.Rsa!.ExportParameters(false).Modulus);
    }

    public void Dispose() => _keys.Delete(recursive: true);
}
