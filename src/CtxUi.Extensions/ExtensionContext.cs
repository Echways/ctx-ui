using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;

namespace CtxUi.Extensions;

public sealed class ExtensionContext(
    string id,
    PanelEditor panel,
    IServiceCollection services,
    IConfiguration configuration,
    string dataDirectory)
{
    public string Id { get; } = id;

    public PanelEditor Panel { get; } = panel;

    public IServiceCollection Services { get; } = services;

    public IConfiguration Configuration { get; } = configuration;

    public string DataDirectory { get; } = dataDirectory;
}
