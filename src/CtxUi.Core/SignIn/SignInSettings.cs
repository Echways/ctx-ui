namespace CtxUi.Core.SignIn;

public sealed class SignInSettings
{
    public const int DefaultIdleDays = 30;
    public const int MaxIdleDays = 365;

    public int Id { get; set; } = 1;
    public bool Enabled { get; set; }
    public bool KeySignIn { get; set; } = true;
    public int? DefaultMaxDevices { get; set; }
    public bool DefaultDirectKey { get; set; } = true;
    public int IdleDays { get; set; } = DefaultIdleDays;
    public bool SelfRevoke { get; set; } = true;
}
