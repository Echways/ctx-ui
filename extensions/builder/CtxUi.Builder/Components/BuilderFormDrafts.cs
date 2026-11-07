using System.ComponentModel.DataAnnotations;
using CtxUi.Builder.Tools;

namespace CtxUi.Builder.Components;

public sealed class ResourceFormDraft
{
    [Required] public string Uri { get; set; } = string.Empty;

    [Required] public string Name { get; set; } = string.Empty;

    [Required] public string MimeType { get; set; } = BuilderDocument.DefaultMimeType;

    public string? Text { get; set; }

    public static ResourceFormDraft From(BuilderResource resource)
    {
        return new ResourceFormDraft
        {
            Uri = resource.Uri,
            Name = resource.Name,
            MimeType = resource.MimeType,
            Text = resource.Text
        };
    }

    public BuilderResource ToResource()
    {
        return new BuilderResource(Uri.Trim(), Name.Trim(), MimeType.Trim(), Text ?? string.Empty);
    }
}

public sealed class PromptFormDraft
{
    [Required] public string Name { get; set; } = string.Empty;

    public string? Description { get; set; }

    [Required] public string Template { get; set; } = string.Empty;

    public IReadOnlyList<string> Arguments => BuilderDocument.Arguments(Template);

    public static PromptFormDraft From(BuilderPrompt prompt)
    {
        return new PromptFormDraft
        {
            Name = prompt.Name,
            Description = prompt.Description,
            Template = prompt.Template
        };
    }

    public BuilderPrompt ToPrompt()
    {
        return new BuilderPrompt(Name.Trim(), string.IsNullOrWhiteSpace(Description) ? null : Description.Trim(),
            Template);
    }
}
