using System.ComponentModel;
using System.Globalization;
using System.Resources;
using MeowGram.Core.Settings;

namespace MeowGram.Localization;

/// <summary>
/// Строки интерфейса (Strings.resx — русский, Strings.en.resx — английский) с переключением языка на лету.
/// В XAML: <c>Text="{l:Tr Login_QrTitle}"</c>. В коде: <c>Loc.Instance["Login_QrTitle"]</c>.
/// </summary>
public sealed class Loc : INotifyPropertyChanged
{
    public static readonly IReadOnlyList<string> SupportedLanguages = ["ru", "en"];

    internal static readonly ResourceManager Resources = new("MeowGram.Localization.Strings", typeof(Loc).Assembly);

    public static Loc Instance { get; } = new();

    private Loc()
    {
    }

    public CultureInfo Culture { get; private set; } = CultureInfo.GetCultureInfo("ru");

    public string Language => Culture.TwoLetterISOLanguageName;

    /// <summary>Строка по ключу. Нет такого ключа — возвращается сам ключ в скобках, чтобы было видно в UI.</summary>
    public string this[string key] => Resources.GetString(key, Culture) ?? $"[{key}]";

    public event PropertyChangedEventHandler? PropertyChanged;

    public event EventHandler? LanguageChanged;

    public string Format(string key, params object?[] args) => string.Format(Culture, this[key], args);

    /// <summary>Переключает язык (UI-поток). Неизвестный — английский.</summary>
    public void SetLanguage(string language)
    {
        var code = SupportedLanguages.Contains(language) ? language : "en";
        if (code == Language)
            return;

        Culture = CultureInfo.GetCultureInfo(code);
        CultureInfo.CurrentUICulture = Culture;
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs("Item[]"));
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(Language)));
        LanguageChanged?.Invoke(this, EventArgs.Empty);
    }

    /// <summary>Язык из настроек, иначе — язык системы. Без UI, можно из любого потока.</summary>
    public static async Task<string> LoadLanguageAsync(ISettingsStore settings) =>
        await settings.GetAsync<string>(SettingKeys.Language).ConfigureAwait(false)
        ?? DefaultForSystem(CultureInfo.InstalledUICulture);

    public static async Task SetAndSaveAsync(ISettingsStore settings, string language)
    {
        Instance.SetLanguage(language);
        await settings.SetAsync(SettingKeys.Language, Instance.Language).ConfigureAwait(false);
    }

    /// <summary>Русский для систем на русском, украинском, белорусском, казахском; иначе английский.</summary>
    public static string DefaultForSystem(CultureInfo culture) =>
        culture.TwoLetterISOLanguageName is "ru" or "uk" or "be" or "kk" ? "ru" : "en";
}
