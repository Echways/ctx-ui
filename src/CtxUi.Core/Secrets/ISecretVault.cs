namespace CtxUi.Core.Secrets;

public interface ISecretVault
{
    string Seal(string plaintext);

    string? Open(string sealedText);
}
