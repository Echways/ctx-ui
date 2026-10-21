using System.Collections;
using System.Globalization;
using System.Resources;

namespace CtxUi.Gateway.Tests;

public class GatewayTextTests
{
    [Fact]
    public void Every_text_has_a_russian_translation()
    {
        var manager = new ResourceManager("CtxUi.Gateway.GatewayText", typeof(GatewayServices).Assembly);

        Assert.Equal(Keys(manager, CultureInfo.InvariantCulture), Keys(manager, CultureInfo.GetCultureInfo("ru")));
    }

    private static List<string> Keys(ResourceManager manager, CultureInfo culture) =>
        manager.GetResourceSet(culture, createIfNotExists: true, tryParents: false)!
            .Cast<DictionaryEntry>()
            .Select(entry => (string)entry.Key)
            .Order(StringComparer.Ordinal)
            .ToList();
}
