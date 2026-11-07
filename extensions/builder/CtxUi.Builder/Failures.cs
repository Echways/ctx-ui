using Enfilade;

namespace CtxUi.Builder;

public enum FailureKind
{
    Invalid,
    NotFound,
    Conflict,
    Upstream
}

public static class Failures
{
    private const string KindKey = "CtxUi.Builder.FailureKind";

    public static PanelException ServerNotFound()
    {
        return Of(FailureKind.NotFound, BuilderText.ServerNotFound);
    }

    public static PanelException ReleaseNotFound()
    {
        return Of(FailureKind.NotFound, BuilderText.ReleaseNotFound);
    }

    public static PanelException Conflict(string message)
    {
        return Of(FailureKind.Conflict, message);
    }

    public static PanelException Upstream(string message)
    {
        return Of(FailureKind.Upstream, message);
    }

    public static FailureKind KindOf(PanelException failure)
    {
        return failure.Data[KindKey] is FailureKind kind ? kind : FailureKind.Invalid;
    }

    private static PanelException Of(FailureKind kind, string message)
    {
        return new PanelException(message) { Data = { [KindKey] = kind } };
    }
}
