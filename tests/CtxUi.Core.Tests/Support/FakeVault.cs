using System.Text;
using CtxUi.Core.Secrets;

namespace CtxUi.Core.Tests;

public sealed class FakeVault : ISecretVault
{
    private const string Prefix = "sealed:";

    public bool Broken { get; set; }

    public string Seal(string plaintext) => Prefix + Convert.ToBase64String(Encoding.UTF8.GetBytes(plaintext));

    public string? Open(string sealedText) =>
        Broken || !sealedText.StartsWith(Prefix, StringComparison.Ordinal)
            ? null
            : Encoding.UTF8.GetString(Convert.FromBase64String(sealedText[Prefix.Length..]));
}
