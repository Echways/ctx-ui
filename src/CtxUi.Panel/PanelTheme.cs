using MudBlazor;

namespace CtxUi.Panel;

public static class PanelTheme
{
    public const string Night = "#080C1C";
    public const string Deep = "#0F1530";
    public const string Rule = "#1E2648";
    public const string Mist = "#8A90AB";
    public const string Light = "#EEEBE5";
    public const string Klein = "#002FA7";
    public const string KleinGlow = "#4A6CFF";
    public const string Vermilion = "#FF5A3C";
    public const string Ochre = "#E3A63E";

    private static readonly string[] Sans = ["Onest", "ui-sans-serif", "system-ui", "sans-serif"];

    public static readonly MudTheme Theme = new()
    {
        PaletteDark = new PaletteDark
        {
            Black = Night,
            White = "#FFFFFF",
            Primary = Klein,
            PrimaryContrastText = "#FFFFFF",
            Secondary = Mist,
            SecondaryContrastText = Night,
            Tertiary = Light,
            TertiaryContrastText = Night,
            Info = KleinGlow,
            InfoContrastText = Night,
            Success = Light,
            SuccessContrastText = Night,
            Warning = Ochre,
            WarningContrastText = Night,
            Error = Vermilion,
            ErrorContrastText = Night,
            Dark = Deep,
            DarkContrastText = Light,
            TextPrimary = Light,
            TextSecondary = Mist,
            TextDisabled = "rgba(138, 144, 171, 0.5)",
            ActionDefault = Mist,
            ActionDisabled = "rgba(138, 144, 171, 0.5)",
            ActionDisabledBackground = Rule,
            Background = Night,
            BackgroundGray = Deep,
            Surface = Deep,
            DrawerBackground = Night,
            DrawerText = Mist,
            DrawerIcon = Mist,
            AppbarBackground = Night,
            AppbarText = Light,
            LinesDefault = Rule,
            LinesInputs = Rule,
            TableLines = Rule,
            TableStriped = Deep,
            TableHover = Deep,
            Divider = Rule,
            DividerLight = Rule,
            Skeleton = Deep,
            OverlayDark = "rgba(8, 12, 28, 0.6)",
            HoverOpacity = 0.06,
        },
        Typography = new Typography
        {
            Default = new DefaultTypography { FontFamily = Sans, FontSize = "15px", FontWeight = "400", LineHeight = "24px", LetterSpacing = "normal" },
            H1 = new H1Typography { FontFamily = Sans, FontSize = "52px", FontWeight = "200", LineHeight = "60px", LetterSpacing = "-0.035em" },
            H6 = new H6Typography { FontFamily = Sans, FontSize = "20px", FontWeight = "500", LineHeight = "28px", LetterSpacing = "-0.01em" },
            Body1 = new Body1Typography { FontFamily = Sans, FontSize = "15px", FontWeight = "400", LineHeight = "24px", LetterSpacing = "normal" },
            Body2 = new Body2Typography { FontFamily = Sans, FontSize = "13px", FontWeight = "500", LineHeight = "20px", LetterSpacing = "normal" },
            Button = new ButtonTypography { FontFamily = Sans, FontSize = "13px", FontWeight = "600", LineHeight = "20px", LetterSpacing = "normal", TextTransform = "none" },
            Caption = new CaptionTypography { FontFamily = Sans, FontSize = "13px", FontWeight = "500", LineHeight = "20px", LetterSpacing = "normal" },
        },
        LayoutProperties = new LayoutProperties { DefaultBorderRadius = "2px" },
        Shadows = new Shadow { Elevation = Enumerable.Repeat("none", new Shadow().Elevation.Length).ToArray() },
    };
}
