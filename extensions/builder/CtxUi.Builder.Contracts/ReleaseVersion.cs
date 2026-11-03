using System.Reflection;

namespace CtxUi.Builder.Contracts;

public static class ReleaseVersion
{
    public static string Of(Assembly assembly)
    {
        var version = assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion;
        return string.IsNullOrEmpty(version) ? "" : Strip(version);
    }

    public static string Strip(string version)
    {
        var plus = version.IndexOf('+');
        return plus < 0 ? version : version[..plus];
    }
}
