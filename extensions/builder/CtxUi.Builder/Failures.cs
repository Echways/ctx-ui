using Enfilade;

namespace CtxUi.Builder;

public enum FailureKind { Invalid, NotFound, Conflict, Upstream }

public static class Failures
{
    private const string KindKey = "CtxUi.Builder.FailureKind";

    public static PanelException ServerNotFound() => Of(FailureKind.NotFound, BuilderText.ServerNotFound);

    public static PanelException ReleaseNotFound() => Of(FailureKind.NotFound, BuilderText.ReleaseNotFound);

    public static PanelException Conflict(string message) => Of(FailureKind.Conflict, message);

    public static PanelException Upstream(string message) => Of(FailureKind.Upstream, message);

    public static FailureKind KindOf(PanelException failure) => failure.Data[KindKey] is FailureKind kind ? kind : FailureKind.Invalid;

    private static PanelException Of(FailureKind kind, string message) => new(message) { Data = { [KindKey] = kind } };
}
