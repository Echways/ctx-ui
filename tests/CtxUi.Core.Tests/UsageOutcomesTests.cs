using CtxUi.Core.Usage;

namespace CtxUi.Core.Tests;

public class UsageOutcomesTests
{
    [Theory]
    [InlineData(UsageOutcome.Ok, "выполнен")]
    [InlineData(UsageOutcome.UpstreamRejected, "upstream отклонил")]
    [InlineData(UsageOutcome.UpstreamFailed, "сбой upstream")]
    [InlineData(UsageOutcome.ClientAborted, "клиент оборвал")]
    [InlineData(UsageOutcome.QuotaExhausted, "квота исчерпана")]
    public void Outcomes_kept_in_the_log_have_the_words_the_command_line_always_used(UsageOutcome outcome, string words) =>
        Assert.Equal(words, UsageOutcomes.Text(outcome));

    [Fact]
    public void No_outcome_is_shown_by_its_name_in_code() =>
        Assert.All(Enum.GetValues<UsageOutcome>(), outcome => Assert.NotEqual(outcome.ToString(), UsageOutcomes.Text(outcome)));
}
