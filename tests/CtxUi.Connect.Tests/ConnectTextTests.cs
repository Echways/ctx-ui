using System.Collections;
using System.Globalization;
using System.Resources;

namespace CtxUi.Connect.Tests;

public class ConnectTextTests
{
    [Fact]
    public void Every_text_has_a_russian_translation()
    {
        var manager = new ResourceManager("CtxUi.Connect.ConnectText", typeof(ConnectServices).Assembly);

        Assert.Equal(Keys(manager, CultureInfo.InvariantCulture), Keys(manager, CultureInfo.GetCultureInfo("ru")));
    }

    private static List<string> Keys(ResourceManager manager, CultureInfo culture)
    {
        return manager.GetResourceSet(culture, true, false)!
            .Cast<DictionaryEntry>()
            .Select(entry => (string)entry.Key)
            .Order(StringComparer.Ordinal)
            .ToList();
    }
}
