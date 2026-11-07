using System.Net;

namespace CtxUi.Builder.Runner;

public sealed class RunnerException(HttpStatusCode? status, string message, Exception? inner = null, string? problem = null) : Exception(message, inner)
{
    public HttpStatusCode? Status { get; } = status;

    public string? Problem { get; } = problem;
}
