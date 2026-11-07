using System.Net;
using CtxUi.Builder.Contracts;

namespace CtxUi.Builder.Runner;

public enum RunnerProblem
{
    None,
    NotConfigured,
    Unreachable,
    TokenRefused,
    VersionMismatch
}

public sealed record RunnerReport(RunnerProblem Problem, string Reason, RunnerHealth? Health);

public sealed class RunnerAvailability(RunnerSettings settings, RunnerClient client)
{
    public const string InstallCommand =
        "curl -fsSL https://github.com/Echways/ctx-ui/releases/latest/download/builder-install.sh | sudo bash";

    private static readonly TimeSpan Timeout = TimeSpan.FromSeconds(5);

    public static string ExtensionVersion { get; } = ReadVersion();

    public async Task<RunnerReport> CheckAsync(CancellationToken ct)
    {
        if (!settings.IsComplete)
            return new RunnerReport(RunnerProblem.NotConfigured,
                string.Format(BuilderText.RunnerNotConfigured, string.Join(", ", settings.Missing)), null);

        using var limit = CancellationTokenSource.CreateLinkedTokenSource(ct);
        limit.CancelAfter(Timeout);
        try
        {
            var health = await client.HealthAsync(limit.Token);
            return health.Version == ExtensionVersion
                ? new RunnerReport(RunnerProblem.None,
                    string.Format(BuilderText.RunnerReady, health.Version, health.DockerVersion), health)
                : new RunnerReport(RunnerProblem.VersionMismatch,
                    string.Format(BuilderText.RunnerVersionMismatch, health.Version, ExtensionVersion), health);
        }
        catch (RunnerException failure) when (failure.Status == HttpStatusCode.Unauthorized)
        {
            return new RunnerReport(RunnerProblem.TokenRefused, string.Format(BuilderText.RunnerRefused, settings.Url),
                null);
        }
        catch (Exception failure) when (failure is RunnerException or OperationCanceledException &&
                                        !ct.IsCancellationRequested)
        {
            return new RunnerReport(RunnerProblem.Unreachable,
                string.Format(BuilderText.RunnerUnreachable, settings.Url,
                    failure is RunnerException ? failure.Message : "timeout"), null);
        }
    }

    private static string ReadVersion()
    {
        return ReleaseVersion.Of(typeof(RunnerAvailability).Assembly);
    }
}
