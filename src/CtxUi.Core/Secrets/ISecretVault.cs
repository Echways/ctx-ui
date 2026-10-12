namespace CtxUi.Core.Secrets;

/// <summary>Обратимое шифрование секретов, которые панель должна уметь показать или использовать снова.</summary>
public interface ISecretVault
{
    string Seal(string plaintext);

    /// <summary>Возвращает null, если значение не расшифровать: ключи потеряны или данные повреждены.</summary>
    string? Open(string sealedText);
}
