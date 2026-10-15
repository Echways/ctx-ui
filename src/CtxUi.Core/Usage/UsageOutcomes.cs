namespace CtxUi.Core.Usage;

public static class UsageOutcomes
{
    public static string Text(UsageOutcome outcome) => outcome switch
    {
        UsageOutcome.Ok => "выполнен",
        UsageOutcome.UpstreamRejected => "upstream отклонил",
        UsageOutcome.UpstreamFailed => "сбой upstream",
        UsageOutcome.ClientAborted => "клиент оборвал",
        UsageOutcome.QuotaExhausted => "квота исчерпана",
        UsageOutcome.RateLimited => "частота превышена",
        UsageOutcome.ConcurrencyLimited => "слишком много одновременных",
        UsageOutcome.BodyTooLarge => "тело слишком большое",
        _ => "не JSON-RPC",
    };
}
