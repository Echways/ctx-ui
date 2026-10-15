namespace CtxUi.Panel;

public static class Words
{
    public static string Sentence(string text) => text.Length == 0 ? text : char.ToUpperInvariant(text[0]) + text[1..];
}
