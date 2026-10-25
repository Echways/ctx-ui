using Microsoft.AspNetCore.DataProtection;
using CtxUi.Core;
using CtxUi.Core.Secrets;
using CtxUi.Core.Usage;
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
            .Validate(
                options => ProxyTrust.IsValid(options.TrustedProxies),
                $"{CtxUiOptions.Section}:TrustedProxies must be IP addresses or networks (10.0.0.0/8) separated by commas")
            .ValidateOnStart();
        var keysDir = KeysPath(configuration);
        var dataDir = Path.GetDirectoryName(keysDir)!;
        CreatePrivateDirectory(dataDir);
        CreatePrivateDirectory(keysDir);

        services.AddCtxUiCore(dataDir);
        services.Configure<UsageOptions>(section);
        services.AddDataProtection()
            .SetApplicationName("CtxUi")
            .PersistKeysToFileSystem(new DirectoryInfo(keysDir));
        services.AddSingleton<ISecretVault, DataProtectionVault>();
        services.AddCtxUiAccounts();
        return services;
    }

    public static string KeysPath(IConfiguration configuration) => Path.Combine(
        Path.GetFullPath((configuration.GetSection(CtxUiOptions.Section).Get<CtxUiOptions>() ?? new CtxUiOptions()).DataDir),
        KeysDirectory);

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
