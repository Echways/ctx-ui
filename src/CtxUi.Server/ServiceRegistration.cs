using Microsoft.AspNetCore.DataProtection;
using CtxUi.Core;
using CtxUi.Core.Secrets;

namespace CtxUi.Server;

public static class ServiceRegistration
{
    private const UnixFileMode OwnerOnly = UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute;

    /// <summary>Общая регистрация для сервера и CLI: оба должны видеть одну базу и одни ключи.</summary>
    public static IServiceCollection AddCtxUi(this IServiceCollection services, IConfiguration configuration)
    {
        var section = configuration.GetSection(CtxUiOptions.Section);
        services.Configure<CtxUiOptions>(section);
        var options = section.Get<CtxUiOptions>() ?? new CtxUiOptions();

        var dataDir = Path.GetFullPath(options.DataDir);
        var keysDir = Path.Combine(dataDir, "keys");
        CreatePrivateDirectory(dataDir);
        CreatePrivateDirectory(keysDir);

        services.AddCtxUiCore(dataDir);
        services.AddDataProtection()
            .SetApplicationName("CtxUi")
            .PersistKeysToFileSystem(new DirectoryInfo(keysDir));
        services.AddSingleton<ISecretVault, DataProtectionVault>();
        return services;
    }

    /// <summary>
    /// Глушит категории, которые пишут в журнал исходный адрес запроса или шумят на каждом обращении.
    /// Задано кодом, а не в appsettings.json, чтобы защита от утечки токена не зависела от файла настроек.
    /// </summary>
    public static ILoggingBuilder AddCtxUiFilters(this ILoggingBuilder logging) => logging
        .AddFilter("Microsoft.AspNetCore.Hosting.Diagnostics", LogLevel.Warning)
        .AddFilter("Microsoft.AspNetCore.DataProtection", LogLevel.Error)
        .AddFilter("Microsoft.EntityFrameworkCore", LogLevel.Warning)
        .AddFilter("Yarp", LogLevel.Warning);

    private static void CreatePrivateDirectory(string path)
    {
        if (OperatingSystem.IsWindows())
            Directory.CreateDirectory(path);
        else
            Directory.CreateDirectory(path, OwnerOnly);
    }
}
