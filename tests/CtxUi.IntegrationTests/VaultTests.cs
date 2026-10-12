using Microsoft.AspNetCore.DataProtection;
using CtxUi.Server;

namespace CtxUi.IntegrationTests;

public class VaultTests
{
    private static DataProtectionVault VaultWithKeysIn(string directory) =>
        new(DataProtectionProvider.Create(new DirectoryInfo(directory)));

    [Fact]
    public void Sealed_value_opens_with_the_same_keys()
    {
        var keys = Directory.CreateTempSubdirectory("ctx-ui-keys-").FullName;
        var sealedText = VaultWithKeysIn(keys).Seal("tnk_секрет");

        Assert.DoesNotContain("секрет", sealedText);
        Assert.Equal("tnk_секрет", VaultWithKeysIn(keys).Open(sealedText));
    }

    [Fact]
    public void Value_sealed_with_lost_keys_opens_as_null()
    {
        var sealedText = VaultWithKeysIn(Directory.CreateTempSubdirectory("ctx-ui-keys-").FullName).Seal("s3cret");

        Assert.Null(VaultWithKeysIn(Directory.CreateTempSubdirectory("ctx-ui-keys-").FullName).Open(sealedText));
        Assert.Null(VaultWithKeysIn(Directory.CreateTempSubdirectory("ctx-ui-keys-").FullName).Open("мусор"));
    }
}
