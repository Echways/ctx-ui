using Microsoft.AspNetCore.DataProtection;
using CtxUi.Core;
using CtxUi.Core.Secrets;
using CtxUi.Panel;

namespace CtxUi.Server;

public static class ServiceRegistration
{
    public const string KeysDirectory = "keys";

    private const UnixFileMode OwnerOnly = UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute;

    public static IServiceCollection AddCtxUi(this IServiceCollection services, IConfiguration configuration)
    {
        var section = configuration.GetSection(CtxUiOptions.Section);
        services.AddOptions<CtxUiOptions>()
            .Bind(section)
            .Validate(
                options => Languages.IsSupported(options.Language),
                $"{CtxUiOptions.Section}:Language must be one of: {string.Join(", ", Languages.Supported)}")
            .ValidateOnStart();
        var options = section.Get<CtxUiOptions>() ?? new CtxUiOptions();

        var dataDir = Path.GetFullPath(options.DataDir);
        var keysDir = Path.Combine(dataDir, KeysDirectory);
        CreatePrivateDirectory(dataDir);
        CreatePrivateDirectory(keysDir);

        services.AddCtxUiCore(dataDir);
        services.AddDataProtection()
            .SetApplicationName("CtxUi")
            .PersistKeysToFileSystem(new DirectoryInfo(keysDir));
        services.AddSingleton<ISecretVault, DataProtectionVault>();
        services.AddCtxUiAccounts();
        return services;
    }

    public static ILoggingBuilder AddCtxUiFilters(this ILoggingBuilder logging) => logging
        .AddFilter("Microsoft.AspNetCore.Hosting.Diagnostics", LogLevel.Warning)
        .AddFilter("Microsoft.AspNetCore.DataProtection", LogLevel.Error)
        .AddFilter("Microsoft.EntityFrameworkCore", LogLevel.Warning)
        .AddFilter("Yarp", LogLevel.Warning);

    internal static void CreatePrivateDirectory(string path)
    {
        if (OperatingSystem.IsWindows())
        {
            Directory.CreateDirectory(path);
            return;
        }

        Directory.CreateDirectory(path, OwnerOnly);
        if (File.GetUnixFileMode(path) != OwnerOnly)
            File.SetUnixFileMode(path, OwnerOnly);
    }
}
