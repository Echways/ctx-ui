namespace CtxUi.Panel;

public sealed record ConnectionMode(bool OAuth, bool KeySignIn, bool DirectKey)
{
    public static ConnectionMode Off { get; } = new(false, true, true);

    public bool ShowsToken => !OAuth || DirectKey || KeySignIn;
}
