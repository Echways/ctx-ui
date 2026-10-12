namespace CtxUi.IntegrationTests;

internal static class Eventually
{
    public static async Task TrueAsync(Func<Task<bool>> condition, string because)
    {
        var deadline = DateTime.UtcNow.AddSeconds(5);
        while (DateTime.UtcNow < deadline)
        {
            if (await condition())
                return;

            await Task.Delay(50);
        }

        Assert.Fail($"за 5 секунд не дождались: {because}");
    }
}
