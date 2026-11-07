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

    public static string ConnectionBusy => Read();

    public static string InboundRejected => Read();

    public static string InboundSlugTaken => Read();

    public static string ProjectTooLarge => Read();

    public static string PublishBaseUrlRequired => Read();

    public static string PublishBusy => Read();

    public static string PublishInterrupted => Read();

    public static string ServerDeleting => Read();

    public static string ServerNotFound => Read();

    public static string RunnerDeleteFailed => Read();

    public static string RunnerForgotRelease => Read();

    public static string RunnerLostDuringBuild => Read();

    public static string RunnerNotConfigured => Read();

    public static string ResponseExampleEmpty => Read();

    public static string ResponseExampleInvalid => Read();

    public static string OpenApiInvalid => Read();

    public static string ImportBroken => Read();

    public static string ImportEmpty => Read();

    public static string ImportUnsupported => Read();

    public static string ImportNoOperations => Read();

    public static string ImportTooLarge => Read();

    public static string ImportTimedOut => Read();

    public static string ImportFetchFailed => Read();

    public static string ImportAddressInvalid => Read();

    public static string ImportNameTaken => Read();

    public static string ImportNothingSelected => Read();

    public static string ToolExists => Read();

    public static string ToolRouteTaken => Read();

    public static string DraftChangedElsewhere => Read();

    public static string ImportHostNotFound => Read();

    public static string ImportTlsFailed => Read();

    public static string ImportUnreachable => Read();

    public static string ImportInterrupted => Read();

    public static string OpenApiVersionMismatch => Read();

    public static string ToolNameTaken => Read();

    public static string ToolBodyNotEditable => Read();

    public static string ArchiveOpenApiInvalid => Read();

    public static string CodeToolNameTaken => Read();

    private static string Read([System.Runtime.CompilerServices.CallerMemberName] string name = "") =>
        (string)Source.GetProperty(name, BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic)!.GetValue(null)!;
}
