using Avalonia;
using Avalonia.Media;
using Avalonia.Styling;
using MeowGram.Core.Settings;

namespace MeowGram.Services;

public enum ThemeMode
{
    System,
    Dark,
    Light,
}

public enum AnimationLevel
{
    /// <summary>Все анимации интерфейса.</summary>
    Full,

    /// <summary>Короче и без движения: только плавное проявление.</summary>
    Reduced,

    /// <summary>Мгновенные переходы.</summary>
    Off,
}

/// <summary>Акцент на выбор пользователя. Все варианты проходят контраст ≥ 4.5:1 с белым текстом (ThemeContrastTests).</summary>
public sealed record AccentPreset(string Id, Color Color)
{
    public static readonly AccentPreset Lavender = new("lavender", Color.Parse("#594BA0"));

    public static IReadOnlyList<AccentPreset> All { get; } =
    [
        Lavender,
        new("lilac", Color.Parse("#7E4FB3")),
        new("ocean", Color.Parse("#2F6FDE")),
        new("mint", Color.Parse("#237A67")),
        new("rose", Color.Parse("#B84D7A")),
        new("amber", Color.Parse("#9A6418")),
    ];

    public static AccentPreset FromId(string? id) => All.FirstOrDefault(a => a.Id == id) ?? Lavender;

    /// <summary>Наведение — +10% к белому.</summary>
    public Color Hover => Mix(Color, Colors.White, 0.10);

    /// <summary>Нажатие — +15% к чёрному.</summary>
    public Color Pressed => Mix(Color, Colors.Black, 0.15);

    private static Color Mix(Color a, Color b, double t) => Color.FromRgb(
        (byte)Math.Round(a.R + (b.R - a.R) * t),
        (byte)Math.Round(a.G + (b.G - a.G) * t),
        (byte)Math.Round(a.B + (b.B - a.B) * t));
}

public sealed record ThemePreferences(ThemeMode Mode, AccentPreset Accent, AnimationLevel Animations)
{
    public static ThemePreferences Default { get; } = new(ThemeMode.Dark, AccentPreset.Lavender, AnimationLevel.Full);
}

/// <summary>
/// Тема (тёмная/светлая/как в системе), акцент и уровень анимаций: применяет на лету и сохраняет в настройках.
/// <see cref="Apply"/> и Set*-методы вызывать из UI-потока.
/// </summary>
public sealed class ThemeService(ISettingsStore settings)
{
    public ThemeMode Mode { get; private set; } = ThemeMode.Dark;

    public AccentPreset Accent { get; private set; } = AccentPreset.Lavender;

    public AnimationLevel Animations => Motion.Level;

    public event EventHandler? Changed;

    /// <summary>Читает сохранённые настройки (без UI, можно из любого потока). Значения хранятся строками.</summary>
    public async Task<ThemePreferences> LoadAsync()
    {
        var mode = await settings.GetAsync<string>(SettingKeys.Theme).ConfigureAwait(false);
        var accent = await settings.GetAsync<string>(SettingKeys.Accent).ConfigureAwait(false);
        var animations = await settings.GetAsync<string>(SettingKeys.AnimationLevel).ConfigureAwait(false);

        return new ThemePreferences(
            Enum.TryParse<ThemeMode>(mode, out var m) ? m : ThemePreferences.Default.Mode,
            AccentPreset.FromId(accent),
            Enum.TryParse<AnimationLevel>(animations, out var a) ? a : ThemePreferences.Default.Animations);
    }

    public void Apply(ThemePreferences preferences)
    {
        ApplyMode(preferences.Mode);
        ApplyAccent(preferences.Accent);
        Motion.Level = preferences.Animations;
        Changed?.Invoke(this, EventArgs.Empty);
    }

    public async Task SetModeAsync(ThemeMode mode)
    {
        ApplyMode(mode);
        Changed?.Invoke(this, EventArgs.Empty);
        await settings.SetAsync(SettingKeys.Theme, mode.ToString()).ConfigureAwait(false);
    }

    public async Task SetAccentAsync(AccentPreset accent)
    {
        ApplyAccent(accent);
        Changed?.Invoke(this, EventArgs.Empty);
        await settings.SetAsync(SettingKeys.Accent, accent.Id).ConfigureAwait(false);
    }

    public async Task SetAnimationsAsync(AnimationLevel level)
    {
        Motion.Level = level;
        Changed?.Invoke(this, EventArgs.Empty);
        await settings.SetAsync(SettingKeys.AnimationLevel, level.ToString()).ConfigureAwait(false);
    }

    private void ApplyMode(ThemeMode mode)
    {
        Mode = mode;
        if (Application.Current is { } app)
        {
            app.RequestedThemeVariant = mode switch
            {
                ThemeMode.Dark => ThemeVariant.Dark,
                ThemeMode.Light => ThemeVariant.Light,
                _ => ThemeVariant.Default,
            };
        }
    }

    private void ApplyAccent(AccentPreset accent)
    {
        Accent = accent;
        if (Application.Current is not { } app)
            return;

        // Записи верхнего уровня Application.Resources важнее словарей тем — акцент один для обеих тем.
        var resources = app.Resources;
        resources["Mg.AccentBrush"] = new SolidColorBrush(accent.Color);
        resources["Mg.AccentHoverBrush"] = new SolidColorBrush(accent.Hover);
        resources["Mg.AccentPressedBrush"] = new SolidColorBrush(accent.Pressed);

        // Стандартные контролы Fluent (фокус, выделение текста) — в тон акценту.
        resources["SystemAccentColor"] = accent.Color;
        resources["SystemAccentColorLight1"] = accent.Hover;
        resources["SystemAccentColorDark1"] = accent.Pressed;
    }
}

/// <summary>Длительности анимаций с учётом выбранного уровня. Используют все анимированные контролы.</summary>
public static class Motion
{
    public static AnimationLevel Level { get; set; } = AnimationLevel.Full;

    public static bool IsOff => Level == AnimationLevel.Off;

    /// <summary>Движение (наклоны, полёты, падения) — только при полных анимациях; иначе лишь проявление.</summary>
    public static bool AllowsMovement => Level == AnimationLevel.Full;

    public static TimeSpan Scale(TimeSpan duration) => Level switch
    {
        AnimationLevel.Full => duration,
        AnimationLevel.Reduced => TimeSpan.FromTicks(Math.Min(duration.Ticks, TimeSpan.FromMilliseconds(150).Ticks)),
        _ => TimeSpan.Zero,
    };

    public static TimeSpan Ms(double milliseconds) => Scale(TimeSpan.FromMilliseconds(milliseconds));
}
