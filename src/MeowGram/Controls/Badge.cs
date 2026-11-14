using Avalonia;
using Avalonia.Controls.Primitives;

namespace MeowGram.Controls;

public enum BadgeKind
{
    /// <summary>Непрочитанные: цвет акцента.</summary>
    Accent,

    /// <summary>Непрочитанные в чате без звука: Mg.Line / Mg.Text.</summary>
    Muted,

    /// <summary>Упоминание: кружок «@» в цвет акцента, <see cref="Badge.Text"/> не нужен.</summary>
    Mention,
}

/// <summary>
/// Счётчик-пилюля списка чатов: высота 22, ширина от 22, скругление 11, Onest 12 SemiBold.
/// Вид задаёт <see cref="Kind"/>; класс <c>small</c> — маленький счётчик на папке рейла (18, скругление 9, кегль 11).
/// Оформление — Themes/Controls.axaml.
/// </summary>
public sealed class Badge : TemplatedControl
{
    public static readonly StyledProperty<string?> TextProperty =
        AvaloniaProperty.Register<Badge, string?>(nameof(Text));

    public static readonly StyledProperty<BadgeKind> KindProperty =
        AvaloniaProperty.Register<Badge, BadgeKind>(nameof(Kind));

    /// <summary>Число непрочитанных уже в готовом виде («3», «99+»).</summary>
    public string? Text
    {
        get => GetValue(TextProperty);
        set => SetValue(TextProperty, value);
    }

    public BadgeKind Kind
    {
        get => GetValue(KindProperty);
        set => SetValue(KindProperty, value);
    }
}
