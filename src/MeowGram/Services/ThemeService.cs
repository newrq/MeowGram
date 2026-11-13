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

/// <summary>
/// Акцент на выбор пользователя. <see cref="Mono"/> (по умолчанию) — монохром из палитры, свой в каждой теме
/// (Tokens.axaml). Цветные — поверх обеих тем, белый текст на них ≥ 4.5:1.
/// </summary>
public sealed record AccentPreset(string Id, Color? Color)
{
    public static readonly AccentPreset Mono = new("mono", null);

    public static readonly AccentPreset Lavender = new("lavender", Avalonia.Media.Color.Parse("#594BA0"));

    public static IReadOnlyList<AccentPreset> All { get; } =
    [
        Mono,
        Lavender,
        new("lilac", Avalonia.Media.Color.Parse("#7E4FB3")),
        new("ocean", Avalonia.Media.Color.Parse("#2F6FDE")),
        new("mint", Avalonia.Media.Color.Parse("#237A67")),
        new("rose", Avalonia.Media.Color.Parse("#B84D7A")),
        new("amber", Avalonia.Media.Color.Parse("#9A6418")),
    ];

    public static AccentPreset FromId(string? id) => All.FirstOrDefault(a => a.Id == id) ?? Mono;

    public bool IsMono => Color is null;

    /// <summary>Наведение — +10% к белому.</summary>
    public Avalonia.Media.Color Hover => Mix(Color ?? Colors.Gray, Colors.White, 0.10);

    /// <summary>Нажатие — +15% к чёрному.</summary>
    public Avalonia.Media.Color Pressed => Mix(Color ?? Colors.Gray, Colors.Black, 0.15);

    /// <summary>Выделение текста — акцент с прозрачностью 45%.</summary>
    public Avalonia.Media.Color Selection => Avalonia.Media.Color.FromArgb(0x73, (Color ?? Colors.Gray).R, (Color ?? Colors.Gray).G, (Color ?? Colors.Gray).B);

    private static Avalonia.Media.Color Mix(Avalonia.Media.Color a, Avalonia.Media.Color b, double t) => Avalonia.Media.Color.FromRgb(
        (byte)Math.Round(a.R + (b.R - a.R) * t),
        (byte)Math.Round(a.G + (b.G - a.G) * t),
        (byte)Math.Round(a.B + (b.B - a.B) * t));
}

public sealed record ThemePreferences(ThemeMode Mode, AccentPreset Accent, AnimationLevel Animations)
{
    public static ThemePreferences Default { get; } = new(ThemeMode.Dark, AccentPreset.Mono, AnimationLevel.Full);
}

/// <summary>
/// Тема (тёмная/светлая/как в системе), акцент и уровень анимаций: применяет на лету и сохраняет в настройках.
/// <see cref="Apply"/> и Set*-методы вызывать из UI-потока.
/// </summary>
public sealed class ThemeService(ISettingsStore settings)
{
    public ThemeMode Mode { get; private set; } = ThemeMode.Dark;

    public AccentPreset Accent { get; private set; } = AccentPreset.Mono;

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

        string[] overridable =
        [
            "Mg.AccentBrush", "Mg.AccentHoverBrush", "Mg.AccentPressedBrush", "Mg.OnAccentBrush", "Mg.SelectionBrush",
            "SystemAccentColor", "SystemAccentColorLight1", "SystemAccentColorDark1",
        ];

        var resources = app.Resources;
        if (accent.Color is not { } color)
        {
            // Монохром: работают значения из словарей тем (у тёмной и светлой они разные).
            foreach (var key in overridable)
                resources.Remove(key);
            return;
        }

        // Цветной акцент: записи верхнего уровня Application.Resources важнее словарей тем — один для обеих тем.
        resources["Mg.AccentBrush"] = new SolidColorBrush(color);
        resources["Mg.AccentHoverBrush"] = new SolidColorBrush(accent.Hover);
        resources["Mg.AccentPressedBrush"] = new SolidColorBrush(accent.Pressed);
        resources["Mg.OnAccentBrush"] = new SolidColorBrush(Color.Parse("#FBFAFB"));
        resources["Mg.SelectionBrush"] = new SolidColorBrush(accent.Selection);

        // Стандартные контролы Fluent (фокус, выделение текста) — в тон акценту.
        resources["SystemAccentColor"] = color;
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
