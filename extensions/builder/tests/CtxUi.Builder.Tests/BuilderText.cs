using System.Reflection;

namespace CtxUi.Builder.Tests;

internal static class BuilderText
{
    private static readonly Type Source = typeof(BuilderEntry).Assembly.GetType("CtxUi.Builder.BuilderText")!;

    public static string ArchiveCorrupted => Read();

    public static string ArchiveMissing => Read();

    public static string CheckFailed => Read();

    public static string CheckTimedOut => Read();

    public static string ConnectionAlreadyConnected => Read();

    public static string InboundRejected => Read();

    public static string InboundSlugTaken => Read();

    public static string PublishBaseUrlRequired => Read();

    public static string PublishBusy => Read();

    public static string PublishInterrupted => Read();

    public static string RunnerDeleteFailed => Read();

    public static string RunnerForgotRelease => Read();

    public static string RunnerLostDuringBuild => Read();

    public static string RunnerNotConfigured => Read();

    private static string Read([System.Runtime.CompilerServices.CallerMemberName] string name = "") =>
        (string)Source.GetProperty(name, BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic)!.GetValue(null)!;
}
