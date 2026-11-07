namespace CtxUi.Builder;

public enum FailureKind
{
    Invalid,
    NotFound,
    Conflict,
    Upstream
}

public sealed class BuilderException(string message, FailureKind kind = FailureKind.Invalid) : Exception(message)
{
    public FailureKind Kind { get; } = kind;
}
