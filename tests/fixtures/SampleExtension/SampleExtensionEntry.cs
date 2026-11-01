using Microsoft.Extensions.Configuration;
using CtxUi.Extensions;
using SampleExtension.Components;

namespace SampleExtension;

public sealed class SampleExtensionEntry : IPanelExtension
{
    public void Register(ExtensionContext context)
    {
        if (context.Configuration.GetValue<bool>("Fail"))
            throw new InvalidOperationException("Sample register failure");

        context.Panel.AddStylesheet("sample.css");
        if (!context.Configuration.GetValue<bool>("Reshape"))
        {
            context.Panel.AddSection<SamplePage>(() => SampleText.Section);
            return;
        }

        context.Panel
            .AddSection<SamplePage>(() => SampleText.Section, PanelPosition.After(PanelSections.Overview))
            .Hide(PanelSections.Journal)
            .Rename(PanelSections.Clients, () => SampleText.Customers)
            .Customize(look =>
            {
                look.Brand = "Sample brand";
                look.Accent = "#123456";
            });
    }
}
