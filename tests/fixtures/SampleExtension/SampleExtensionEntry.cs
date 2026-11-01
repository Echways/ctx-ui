using CtxUi.Extensions;
using SampleExtension.Components;

namespace SampleExtension;

public sealed class SampleExtensionEntry : IPanelExtension
{
    public void Register(ExtensionContext context)
    {
        context.Panel.AddSection<SamplePage>(() => SampleText.Section);
    }
}
