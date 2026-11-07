namespace CtxUi.Builder;

public static class Failures
{
    public static BuilderException ServerNotFound()
    {
        return new BuilderException(BuilderText.ServerNotFound, FailureKind.NotFound);
    }

    public static BuilderException ReleaseNotFound()
    {
        return new BuilderException(BuilderText.ReleaseNotFound, FailureKind.NotFound);
    }

    public static BuilderException Conflict(string message)
    {
        return new BuilderException(message, FailureKind.Conflict);
    }

    public static BuilderException Upstream(string message)
    {
        return new BuilderException(message, FailureKind.Upstream);
    }
}
