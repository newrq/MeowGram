using Avalonia;
using Avalonia.Media;
using Avalonia.Media.Fonts;

namespace MeowGram;

sealed class Program
{
    /// <summary>Встроенные шрифты: <c>fonts:MeowGram#Onest</c>, <c>fonts:MeowGram#Playfair Display</c>.</summary>
    public const string FontCollectionKey = "fonts:MeowGram";

    // Initialization code. Don't use any Avalonia, third-party APIs or any
    // SynchronizationContext-reliant code before AppMain is called: things aren't initialized
    // yet and stuff might break.
    [STAThread]
    public static void Main(string[] args) => BuildAvaloniaApp()
        .StartWithClassicDesktopLifetime(args);

    // Avalonia configuration, don't remove; also used by visual designer.
    public static AppBuilder BuildAvaloniaApp()
        => AppBuilder.Configure<App>()
            .UsePlatformDetect()
#if DEBUG
            .WithDeveloperTools()
#endif
            .WithMeowGramFonts()
            .LogToTrace();
}

public static class AppBuilderFontExtensions
{
    /// <summary>Регистрирует Onest и Playfair Display из Assets/Fonts; Onest — шрифт по умолчанию для всех контролов.</summary>
    public static AppBuilder WithMeowGramFonts(this AppBuilder builder) => builder
        .ConfigureFonts(fonts => fonts.AddFontCollection(new EmbeddedFontCollection(
            new Uri(Program.FontCollectionKey, UriKind.Absolute),
            new Uri("avares://MeowGram/Assets/Fonts", UriKind.Absolute))))
        .With(new FontManagerOptions { DefaultFamilyName = Program.FontCollectionKey + "#Onest" });
}
