using CtxUi.Core.SignIn;

namespace CtxUi.Core.Settings;

public static class PublicAddress
{
    public static string? Normalize(string? value)
    {
        value = value?.Trim().TrimEnd('/');
        var uri = Parse(value);
        return uri is not null && PathOf(value!).Length == 0 &&
               OAuthAvailability.IsAllowed(uri.AbsoluteUri)
            ? Authority(uri)
            : null;
    }

    internal static Uri? Parse(string? value)
    {
        if (value is null || value.Any(char.IsWhiteSpace) || value.Any(char.IsControl) ||
            value.AsSpan().IndexOfAny("\\?#%@") >= 0 ||
            !Uri.TryCreate(value, UriKind.Absolute, out var uri) ||
            uri.Scheme is not ("https" or "http") ||
            !value.StartsWith(uri.Scheme + "://", StringComparison.OrdinalIgnoreCase) ||
            uri.HostNameType is not (UriHostNameType.Dns or UriHostNameType.IPv4 or UriHostNameType.IPv6) ||
            uri.Host.EndsWith('.') || uri.Port == 0)
            return null;

        var authority = value.AsSpan((uri.Scheme.Length + 3)..);
        var slash = authority.IndexOf('/');
        if (slash >= 0)
            authority = authority[..slash];
        if (authority.IsEmpty || authority[^1] == ':' ||
            uri.HostNameType == UriHostNameType.IPv4 && !authority.SequenceEqual(uri.Host) &&
            !authority.StartsWith(uri.Host + ":", StringComparison.Ordinal))
            return null;

        if (uri.HostNameType == UriHostNameType.Dns && uri.IdnHost.Split('.').Any(label =>
                label.Length is 0 or > 63 || label[0] == '-' || label[^1] == '-' ||
                label.Any(character => !char.IsAsciiLetterOrDigit(character) && character != '-')))
            return null;

        return uri;
    }

    internal static string PathOf(string url)
    {
        var start = url.IndexOf('/', url.IndexOf("://", StringComparison.Ordinal) + 3);
        return start < 0 ? "" : url[start..];
    }

    internal static string Authority(Uri uri)
    {
        var host = uri.HostNameType == UriHostNameType.IPv6 ? uri.Host : uri.IdnHost.ToLowerInvariant();
        return uri.IsDefaultPort ? $"{uri.Scheme}://{host}" : $"{uri.Scheme}://{host}:{uri.Port}";
    }
}
