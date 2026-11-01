using System.Xml.Linq;

namespace CtxUi.Builder.Tests;

public sealed class TextTests
{
    private static HashSet<string> Names(string file) =>
        XDocument.Load(Path.Combine(AppContext.BaseDirectory, file)).Descendants("data").Select(data => data.Attribute("name")!.Value).ToHashSet();

    [Fact]
    public void Russian_text_covers_every_english_key()
    {
        var english = Names("BuilderText.resx");
        var russian = Names("BuilderText.ru.resx");
        Assert.NotEmpty(english);
        Assert.Equal(english, russian);
    }
}
