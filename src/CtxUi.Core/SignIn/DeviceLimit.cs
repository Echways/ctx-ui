namespace CtxUi.Core.SignIn;

public static class DeviceLimit
{
    public const int Ceiling = 1_000;

    public static int? Validated(int? value) =>
        value is < 1 or > Ceiling ? throw new DomainException(string.Format(CoreText.DeviceLimitOutOfRange, Ceiling)) : value;
}
