using Avalonia;
using Avalonia.Controls;
using Avalonia.Media;
using Avalonia.Styling;
using MeowGram.Core.Settings;

namespace MeowGram.Services;

public enum ThemeMode
{
    Dark,
    Light,
}

public enum AnimationLevel
{
    /// <summary>Все анимации интерфейса.</summary>
    Full,

    /// <summary>Мгновенные переходы.</summary>
    Off,
}

/// <summary>
/// Акцент на выбор пользователя. <see cref="Mono"/> (по умолчанию) — монохром из палитры, свой в каждой теме
/// (Tokens.axaml). Цветные — поверх обеих тем, белый текст на них ≥ 4.5:1. У градиента задан <see cref="To"/>.
/// </summary>
public sealed record AccentPreset(string Id, Color? From, Color? To = null)
{
    public static readonly AccentPreset Mono = new("mono", null);

    public static readonly AccentPreset Pink = new("pink", Color.Parse("#B84D7A"));

    public static readonly AccentPreset Violet = new("violet", Color.Parse("#7E4FB3"));

    public static readonly AccentPreset VioletGradient = new("violetGradient", Color.Parse("#594BA0"), Color.Parse("#7E4FB3"));

    public static IReadOnlyList<AccentPreset> All { get; } = [Mono, Pink, Violet, VioletGradient];

    public static AccentPreset FromId(string? id) => All.FirstOrDefault(a => a.Id == id) ?? Mono;

    public bool IsMono => From is null;

    /// <summary>Один цвет для мест, где градиент не нужен (выделение текста, контролы Fluent): у градиента — середина.</summary>
    public Color Middle => To is { } to ? ColorMath.Lerp(From ?? Colors.Gray, to, 0.5) : From ?? Colors.Gray;

    /// <summary>Выделение текста — акцент с прозрачностью 45%.</summary>
    public Color Selection => Color.FromArgb(0x73, Middle.R, Middle.G, Middle.B);

    public IBrush CreateBrush() => Build(c => c);

    /// <summary>Наведение — +10% к белому.</summary>
    public IBrush CreateHoverBrush() => Build(c => ColorMath.Lerp(c, Colors.White, 0.10));

    /// <summary>Нажатие — +15% к чёрному.</summary>
    public IBrush CreatePressedBrush() => Build(c => ColorMath.Lerp(c, Colors.Black, 0.15));

    private IBrush Build(Func<Color, Color> shade)
    {
        var from = From ?? Colors.Gray;
        if (To is not { } to)
            return new SolidColorBrush(shade(from));

        return new LinearGradientBrush
        {
            StartPoint = new RelativePoint(0, 0, RelativeUnit.Relative),
            EndPoint = new RelativePoint(1, 1, RelativeUnit.Relative),
            GradientStops = { new GradientStop(shade(from), 0), new GradientStop(shade(to), 1) },
        };
    }
}

public sealed record ThemePreferences(ThemeMode Mode, AccentPreset Accent, AnimationLevel Animations)
{
    public static ThemePreferences Default { get; } = new(ThemeMode.Dark, AccentPreset.Mono, AnimationLevel.Full);
}

/// <summary>
/// Тема (тёмная/светлая), акцент и анимации: применяет на лету и сохраняет в настройках.
/// Пока пользователь тянет ползунок темы, <see cref="PreviewBlend"/> плавно смешивает цвета двух тем.
/// <see cref="Apply"/>, Set*- и Preview-методы вызывать из UI-потока.
/// </summary>
public sealed class ThemeService(ISettingsStore settings)
{
    /// <summary>Цвета акцента: при цветном акценте их кисти задаёт <see cref="ApplyAccent"/>, смешивать их не нужно.</summary>
    private static readonly string[] AccentColorKeys = ["Mg.Accent", "Mg.AccentHover", "Mg.AccentPressed", "Mg.OnAccent", "Mg.Selection"];

    /// <summary>Цвета из Tokens.axaml, у которых есть кисть «ключ + Brush».</summary>
    private static readonly string[] BlendColorKeys =
    [
        "Mg.Bg", "Mg.Surface", "Mg.Inset", "Mg.Line", "Mg.Text", "Mg.TextSecondary", "Mg.TextMuted", "Mg.Error", "Mg.Success",
        .. AccentColorKeys,
    ];

    private readonly List<(SolidColorBrush Brush, Color Dark, Color Light)> _blendBrushes = [];
    private ResourceDictionary? _blend;

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

        // Старые значения («как в системе», «меньше», прежние акценты) не разбираются — берутся значения по умолчанию.
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
        EndPreview();
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

    /// <summary>
    /// Промежуточные цвета между темами: 0 — тёмная, 1 — светлая. Сама тема не меняется и не сохраняется —
    /// это делает <see cref="SetModeAsync"/>, а <see cref="EndPreview"/> просто возвращает цвета текущей темы.
    /// </summary>
    public void PreviewBlend(double toLight)
    {
        if (Application.Current is not { } app)
            return;

        if (_blend is null)
            BeginBlend(app);

        var t = Math.Clamp(toLight, 0, 1);
        foreach (var (brush, dark, light) in _blendBrushes)
            brush.Color = ColorMath.Lerp(dark, light, t);
    }

    public void EndPreview()
    {
        if (_blend is null)
            return;

        Application.Current?.Resources.MergedDictionaries.Remove(_blend);
        _blend = null;
        _blendBrushes.Clear();
    }

    /// <summary>
    /// Свои кисти поверх кистей тем: словарь добавляется последним в MergedDictionaries (одно уведомление дереву),
    /// дальше на каждый кадр меняется только цвет кистей. Цветной акцент лежит в Application.Resources выше и не смешивается.
    /// </summary>
    private void BeginBlend(Application app)
    {
        _blend = new ResourceDictionary();
        foreach (var key in BlendColorKeys)
        {
            if (!Accent.IsMono && AccentColorKeys.Contains(key))
                continue;
            if (!TryGetColor(app, key, ThemeVariant.Dark, out var dark) || !TryGetColor(app, key, ThemeVariant.Light, out var light))
                continue;

            var brush = new SolidColorBrush(Mode == ThemeMode.Light ? light : dark);
            _blend[key + "Brush"] = brush;
            _blendBrushes.Add((brush, dark, light));
        }

        app.Resources.MergedDictionaries.Add(_blend);
    }

    private static bool TryGetColor(Application app, string key, ThemeVariant variant, out Color color)
    {
        if (app.Resources.TryGetResource(key, variant, out var value) && value is Color c)
        {
            color = c;
            return true;
        }

        color = default;
        return false;
    }

    private void ApplyMode(ThemeMode mode)
    {
        Mode = mode;
        if (Application.Current is { } app)
            app.RequestedThemeVariant = mode == ThemeMode.Light ? ThemeVariant.Light : ThemeVariant.Dark;
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
        if (accent.IsMono)
        {
            // Монохром: работают значения из словарей тем (у тёмной и светлой они разные).
            foreach (var key in overridable)
                resources.Remove(key);
            return;
        }

        // Цветной акцент: записи верхнего уровня Application.Resources важнее словарей тем — один для обеих тем.
        resources["Mg.AccentBrush"] = accent.CreateBrush();
        resources["Mg.AccentHoverBrush"] = accent.CreateHoverBrush();
        resources["Mg.AccentPressedBrush"] = accent.CreatePressedBrush();
        resources["Mg.OnAccentBrush"] = new SolidColorBrush(Color.Parse("#FBFAFB"));
        resources["Mg.SelectionBrush"] = new SolidColorBrush(accent.Selection);

        // Стандартные контролы Fluent (фокус, выделение текста) — в тон акценту.
        var middle = accent.Middle;
        resources["SystemAccentColor"] = middle;
        resources["SystemAccentColorLight1"] = ColorMath.Lerp(middle, Colors.White, 0.10);
        resources["SystemAccentColorDark1"] = ColorMath.Lerp(middle, Colors.Black, 0.15);
    }
}

internal static class ColorMath
{
    /// <summary>Линейное смешение по каналам, включая прозрачность.</summary>
    public static Color Lerp(Color a, Color b, double t) => Color.FromArgb(
        Channel(a.A, b.A, t),
        Channel(a.R, b.R, t),
        Channel(a.G, b.G, t),
        Channel(a.B, b.B, t));

    private static byte Channel(byte a, byte b, double t) => (byte)Math.Round(a + (b - a) * t);
}

/// <summary>Длительности анимаций с учётом настройки. Используют все анимированные контролы.</summary>
public static class Motion
{
    public static AnimationLevel Level { get; set; } = AnimationLevel.Full;

    public static bool IsOff => Level == AnimationLevel.Off;

    /// <summary>Движение (наклоны, полёты, падения): при выключенных анимациях — только мгновенная смена.</summary>
    public static bool AllowsMovement => !IsOff;

    public static TimeSpan Scale(TimeSpan duration) => IsOff ? TimeSpan.Zero : duration;

    public static TimeSpan Ms(double milliseconds) => Scale(TimeSpan.FromMilliseconds(milliseconds));
}
