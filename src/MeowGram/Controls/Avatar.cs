using System.Globalization;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Documents;
using Avalonia.Media;
using Avalonia.Media.Imaging;
using Avalonia.Media.Immutable;

namespace MeowGram.Controls;

/// <summary>
/// Круглая аватарка: картинка <see cref="Image"/>, а без неё — инициалы (Onest SemiBold) на фоне <see cref="Background"/>.
/// <see cref="ShowOnline"/> — точка «в сети» (<see cref="OnlineBrush"/>, по умолчанию Mg.OnlineBrush) в правом нижнем углу.
/// Кольцо вокруг точки по умолчанию — вырез в аватарке: сквозь него виден фон под ней (строка списка, выделение, наведение).
/// Если задан <see cref="OnlineRingBrush"/>, кольцо рисуется этим цветом.
/// Размеры — по макету главного окна: аватарка 50 → инициалы 16, точка 14 с кольцом 3; для других размеров — пропорционально.
/// Оформление по умолчанию (шрифт, цвет точки) — Themes/Controls.axaml.
/// </summary>
public sealed class Avatar : Control
{
    /// <summary>Серые заглушки из макета: фон и цвет инициалов.</summary>
    private static readonly (ImmutableSolidColorBrush Background, ImmutableSolidColorBrush Foreground)[] FallbackPalette =
    [
        (Solid("#5E5E5E"), Solid("#F0F0F0")),
        (Solid("#3B3B3B"), Solid("#EDEDED")),
        (Solid("#6B6B6B"), Solid("#F5F5F5")),
        (Solid("#4A4A4A"), Solid("#EDEDED")),
        (Solid("#565656"), Solid("#F0F0F0")),
    ];

    /// <summary>Кегль инициалов по макету: диаметр → размер. Между точками — линейно, за краями — пропорционально.</summary>
    private static readonly (double Size, double Font)[] InitialsScale = [(34, 12.5), (40, 13.5), (44, 15), (50, 16), (96, 30)];

    public static readonly StyledProperty<double> SizeProperty =
        AvaloniaProperty.Register<Avatar, double>(nameof(Size), 50);

    public static readonly StyledProperty<string?> InitialsProperty =
        AvaloniaProperty.Register<Avatar, string?>(nameof(Initials));

    public static readonly StyledProperty<IImage?> ImageProperty =
        AvaloniaProperty.Register<Avatar, IImage?>(nameof(Image));

    public static readonly StyledProperty<IBrush?> BackgroundProperty =
        AvaloniaProperty.Register<Avatar, IBrush?>(nameof(Background), FallbackPalette[0].Background);

    public static readonly StyledProperty<IBrush?> ForegroundProperty =
        AvaloniaProperty.Register<Avatar, IBrush?>(nameof(Foreground), FallbackPalette[0].Foreground);

    public static readonly StyledProperty<FontFamily> FontFamilyProperty =
        TextElement.FontFamilyProperty.AddOwner<Avatar>();

    public static readonly StyledProperty<double> FontSizeProperty =
        AvaloniaProperty.Register<Avatar, double>(nameof(FontSize), double.NaN);

    public static readonly StyledProperty<bool> ShowOnlineProperty =
        AvaloniaProperty.Register<Avatar, bool>(nameof(ShowOnline));

    public static readonly StyledProperty<IBrush?> OnlineBrushProperty =
        AvaloniaProperty.Register<Avatar, IBrush?>(nameof(OnlineBrush));

    public static readonly StyledProperty<IBrush?> OnlineRingBrushProperty =
        AvaloniaProperty.Register<Avatar, IBrush?>(nameof(OnlineRingBrush));

    private FormattedText? _text;
    private double _textDiameter;

    static Avatar()
    {
        AffectsMeasure<Avatar>(SizeProperty);
        AffectsRender<Avatar>(SizeProperty, InitialsProperty, ImageProperty, BackgroundProperty, ForegroundProperty,
            FontFamilyProperty, FontSizeProperty, ShowOnlineProperty, OnlineBrushProperty, OnlineRingBrushProperty);
    }

    public Avatar()
    {
        // Фото обычно крупнее аватарки в разы: без сглаживания при уменьшении появляется «зерно».
        RenderOptions.SetBitmapInterpolationMode(this, BitmapInterpolationMode.HighQuality);
    }

    /// <summary>Диаметр.</summary>
    public double Size
    {
        get => GetValue(SizeProperty);
        set => SetValue(SizeProperty, value);
    }

    /// <summary>Одна-две буквы, когда нет картинки.</summary>
    public string? Initials
    {
        get => GetValue(InitialsProperty);
        set => SetValue(InitialsProperty, value);
    }

    /// <summary>Фото; обрезается по центру до квадрата и вписывается в круг.</summary>
    public IImage? Image
    {
        get => GetValue(ImageProperty);
        set => SetValue(ImageProperty, value);
    }

    /// <summary>Фон под инициалами. Для чатов без фото — <see cref="FallbackBackground"/>.</summary>
    public IBrush? Background
    {
        get => GetValue(BackgroundProperty);
        set => SetValue(BackgroundProperty, value);
    }

    /// <summary>Цвет инициалов. Не наследуется: фон аватарки свой, а не цвет панели. Для заглушек — <see cref="FallbackForeground"/>.</summary>
    public IBrush? Foreground
    {
        get => GetValue(ForegroundProperty);
        set => SetValue(ForegroundProperty, value);
    }

    public FontFamily FontFamily
    {
        get => GetValue(FontFamilyProperty);
        set => SetValue(FontFamilyProperty, value);
    }

    /// <summary>Кегль инициалов; не задан (NaN) — по размеру аватарки, как в макете.</summary>
    public double FontSize
    {
        get => GetValue(FontSizeProperty);
        set => SetValue(FontSizeProperty, value);
    }

    public bool ShowOnline
    {
        get => GetValue(ShowOnlineProperty);
        set => SetValue(ShowOnlineProperty, value);
    }

    /// <summary>Цвет точки «в сети».</summary>
    public IBrush? OnlineBrush
    {
        get => GetValue(OnlineBrushProperty);
        set => SetValue(OnlineBrushProperty, value);
    }

    /// <summary>Цвет кольца вокруг точки. Не задан — кольцо прозрачное (вырез), виден фон под аватаркой.</summary>
    public IBrush? OnlineRingBrush
    {
        get => GetValue(OnlineRingBrushProperty);
        set => SetValue(OnlineRingBrushProperty, value);
    }

    /// <summary>Серый фон-заглушка из палитры макета; для одного id (чат, пользователь) всегда один и тот же.</summary>
    public static IBrush FallbackBackground(long id) => FallbackPalette[FallbackIndex(id)].Background;

    /// <summary>Цвет инициалов к <see cref="FallbackBackground"/> с тем же id.</summary>
    public static IBrush FallbackForeground(long id) => FallbackPalette[FallbackIndex(id)].Foreground;

    protected override void OnPropertyChanged(AvaloniaPropertyChangedEventArgs change)
    {
        base.OnPropertyChanged(change);
        if (change.Property == InitialsProperty || change.Property == ForegroundProperty || change.Property == FontFamilyProperty
            || change.Property == FontSizeProperty || change.Property == SizeProperty)
        {
            _text = null;
        }
    }

    protected override Size MeasureOverride(Size availableSize) => new(Size, Size);

    public override void Render(DrawingContext context)
    {
        var diameter = Math.Min(Bounds.Width, Bounds.Height);
        if (diameter <= 0)
            return;

        var circle = new Rect((Bounds.Width - diameter) / 2, (Bounds.Height - diameter) / 2, diameter, diameter);
        var dot = new Rect(circle.Right - diameter * 0.28, circle.Bottom - diameter * 0.28, diameter * 0.28, diameter * 0.28);
        var ring = diameter * 0.06;

        Geometry shape = new EllipseGeometry(circle);
        if (ShowOnline && OnlineRingBrush is null)
            shape = new CombinedGeometry(GeometryCombineMode.Exclude, shape, new EllipseGeometry(dot));

        if (Image is { } image && image.Size is { Width: > 0, Height: > 0 } size)
        {
            // Фото — по центру, квадратом во весь круг.
            var side = Math.Min(size.Width, size.Height);
            var source = new Rect((size.Width - side) / 2, (size.Height - side) / 2, side, side);
            using (context.PushGeometryClip(shape))
                context.DrawImage(image, source, circle);
        }
        else
        {
            if (Background is { } background)
                context.DrawGeometry(background, null, shape);
            if (GetText(diameter) is { } text)
                context.DrawText(text, new Point(circle.Center.X - text.Width / 2, circle.Center.Y - text.Height / 2));
        }

        if (!ShowOnline)
            return;

        if (OnlineRingBrush is { } ringBrush)
            context.DrawEllipse(ringBrush, null, dot);
        if (OnlineBrush is { } online)
            context.DrawEllipse(online, null, dot.Deflate(ring));
    }

    private FormattedText? GetText(double diameter)
    {
        if (string.IsNullOrWhiteSpace(Initials) || Foreground is null)
            return null;

        // Кегль по умолчанию зависит от фактического диаметра (он может отличаться от Size при заданных Width/Height).
        if (_textDiameter != diameter)
            _text = null;
        _textDiameter = diameter;

        return _text ??= new FormattedText(
            Initials.Trim(),
            CultureInfo.CurrentUICulture,
            FlowDirection.LeftToRight,
            new Typeface(FontFamily, FontStyle.Normal, FontWeight.SemiBold),
            double.IsNaN(FontSize) ? AutoFontSize(diameter) : FontSize,
            Foreground);
    }

    private static double AutoFontSize(double diameter)
    {
        var first = InitialsScale[0];
        if (diameter <= first.Size)
            return diameter * first.Font / first.Size;

        for (var i = 1; i < InitialsScale.Length; i++)
        {
            var (size, font) = InitialsScale[i];
            if (diameter > size)
                continue;

            var (prevSize, prevFont) = InitialsScale[i - 1];
            return prevFont + (font - prevFont) * (diameter - prevSize) / (size - prevSize);
        }

        var last = InitialsScale[^1];
        return diameter * last.Font / last.Size;
    }

    private static int FallbackIndex(long id) => (int)(unchecked((ulong)id) % (ulong)FallbackPalette.Length);

    private static ImmutableSolidColorBrush Solid(string color) => new(Color.Parse(color));
}
