namespace CtxUi.Core.Usage;

public enum UsageOutcome
{
    Ok = 0,
    UpstreamRejected = 1,
    UpstreamFailed = 2,
    ClientAborted = 3,
    QuotaExhausted = 4,
    RateLimited = 5,
    ConcurrencyLimited = 6,
    BodyTooLarge = 7,
    NotJsonRpc = 8,
}
