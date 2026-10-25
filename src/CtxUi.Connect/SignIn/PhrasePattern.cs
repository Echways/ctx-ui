using System.Text.RegularExpressions;

namespace CtxUi.Connect.SignIn;

public static partial class PhrasePattern
{
    public static string[] Split(string pattern) => Placeholder().Split(pattern);

    [GeneratedRegex(@"\{(\d+)\}")]
    private static partial Regex Placeholder();
}
