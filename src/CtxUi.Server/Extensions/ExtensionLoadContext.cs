using System.Reflection;
using System.Runtime.Loader;

namespace CtxUi.Server.Extensions;

public sealed class ExtensionLoadContext : AssemblyLoadContext
{
    private static readonly string[] SharedPrefixes =
    [
        "Enfilade", "MudBlazor", "Jeffijoe.MessageFormat", "Microsoft.AspNetCore", "Microsoft.Extensions",
        "Microsoft.EntityFrameworkCore", "Microsoft.Data.Sqlite", "SQLitePCLRaw", "System."
    ];

    private readonly AssemblyDependencyResolver _resolver;

    public ExtensionLoadContext(string id, string assemblyPath)
        : base(id)
    {
        _resolver = new AssemblyDependencyResolver(assemblyPath);
        Resolving += (_, name) => FromFolder(name);
    }

    protected override Assembly? Load(AssemblyName assemblyName)
    {
        return IsShared(assemblyName.Name!) ? null : FromFolder(assemblyName);
    }

    private static bool IsShared(string name)
    {
        return name == "CtxUi.Extensions" ||
               SharedPrefixes.Any(prefix => name.StartsWith(prefix, StringComparison.Ordinal));
    }

    private Assembly? FromFolder(AssemblyName name)
    {
        var path = _resolver.ResolveAssemblyToPath(name);
        return path is null ? null : LoadFromAssemblyPath(path);
    }
}
