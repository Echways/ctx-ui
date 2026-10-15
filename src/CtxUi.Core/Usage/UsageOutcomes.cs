namespace CtxUi.Core.Usage;

public static class UsageOutcomes
{
    public static string Text(UsageOutcome outcome) => outcome switch
    {
        UsageOutcome.Ok => CoreText.OutcomeOk,
        UsageOutcome.UpstreamRejected => CoreText.OutcomeUpstreamRejected,
        UsageOutcome.UpstreamFailed => CoreText.OutcomeUpstreamFailed,
        UsageOutcome.ClientAborted => CoreText.OutcomeClientAborted,
        UsageOutcome.QuotaExhausted => CoreText.OutcomeQuotaExhausted,
        UsageOutcome.RateLimited => CoreText.OutcomeRateLimited,
        UsageOutcome.ConcurrencyLimited => CoreText.OutcomeConcurrencyLimited,
        UsageOutcome.BodyTooLarge => CoreText.OutcomeBodyTooLarge,
        UsageOutcome.NotJsonRpc => CoreText.OutcomeNotJsonRpc,
        _ => outcome.ToString(),
    };
}
