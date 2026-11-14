using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;

namespace MeowGram.Controls;

/// <summary>
/// Текст пузыря и подпись «время + галочки» (как float:right в макете): если в последней строке текста хватает места,
/// подпись встаёт в её конце, иначе — отдельной строкой справа. Первый дочерний элемент — текст (TextBlock),
/// второй — подпись. Подпись опущена на <see cref="MetaDrop"/> ниже базовой линии текста, как в макете.
/// </summary>
public sealed class InlineMetaPanel : Panel
{
    public static readonly StyledProperty<double> GapProperty =
        AvaloniaProperty.Register<InlineMetaPanel, double>(nameof(Gap), 12);

    public static readonly StyledProperty<double> MetaDropProperty =
        AvaloniaProperty.Register<InlineMetaPanel, double>(nameof(MetaDrop), 5);

    private bool _inline;

    static InlineMetaPanel()
    {
        AffectsMeasure<InlineMetaPanel>(GapProperty, MetaDropProperty);
    }

    public double Gap
    {
        get => GetValue(GapProperty);
        set => SetValue(GapProperty, value);
    }

    public double MetaDrop
    {
        get => GetValue(MetaDropProperty);
        set => SetValue(MetaDropProperty, value);
    }

    protected override Size MeasureOverride(Size availableSize)
    {
        if (Children.Count < 2)
            return Children.Count == 1 ? Measure(Children[0], availableSize) : default;

        var text = Children[0];
        var meta = Children[1];
        meta.Measure(Size.Infinity);
        var metaSize = meta.DesiredSize;

        if (!text.IsVisible || text is TextBlock { Text.Length: 0, Inlines.Count: 0 })
        {
            _inline = false;
            return metaSize;
        }

        text.Measure(availableSize);
        var textSize = text.DesiredSize;
        var lastLine = LastLineWidth(text, textSize.Width);

        if (lastLine + Gap + metaSize.Width <= availableSize.Width)
        {
            _inline = true;
            return new Size(Math.Max(textSize.Width, lastLine + Gap + metaSize.Width), textSize.Height);
        }

        _inline = false;
        return new Size(Math.Max(textSize.Width, metaSize.Width), textSize.Height + metaSize.Height);
    }

    protected override Size ArrangeOverride(Size finalSize)
    {
        if (Children.Count < 2)
        {
            if (Children.Count == 1)
                Children[0].Arrange(new Rect(finalSize));
            return finalSize;
        }

        var text = Children[0];
        var meta = Children[1];
        var metaSize = meta.DesiredSize;
        text.Arrange(new Rect(0, 0, finalSize.Width, text.DesiredSize.Height));

        var y = _inline
            ? text.DesiredSize.Height - metaSize.Height + MetaDrop
            : finalSize.Height - metaSize.Height;
        meta.Arrange(new Rect(finalSize.Width - metaSize.Width, Math.Max(0, y), metaSize.Width, metaSize.Height));
        return finalSize;
    }

    private static Size Measure(Control child, Size available)
    {
        child.Measure(available);
        return child.DesiredSize;
    }

    private static double LastLineWidth(Control text, double fallback)
    {
        if (text is not TextBlock block)
            return fallback;

        var lines = block.TextLayout.TextLines;
        return lines.Count == 0 ? 0 : lines[^1].WidthIncludingTrailingWhitespace + block.Padding.Left;
    }
}

/// <summary>
/// Строка ленты с одним пузырём: ширина не больше <see cref="MaxBubbleWidth"/> и <see cref="MaxFraction"/> ширины ленты
/// (min(520px, 80%) в макете), прижат влево или вправо (<see cref="AlignRight"/>).
/// </summary>
public sealed class BubbleRowPanel : Panel
{
    public static readonly StyledProperty<double> MaxBubbleWidthProperty =
        AvaloniaProperty.Register<BubbleRowPanel, double>(nameof(MaxBubbleWidth), 520);

    public static readonly StyledProperty<double> MaxFractionProperty =
        AvaloniaProperty.Register<BubbleRowPanel, double>(nameof(MaxFraction), 0.8);

    public static readonly StyledProperty<bool> AlignRightProperty =
        AvaloniaProperty.Register<BubbleRowPanel, bool>(nameof(AlignRight));

    static BubbleRowPanel()
    {
        AffectsMeasure<BubbleRowPanel>(MaxBubbleWidthProperty, MaxFractionProperty);
        AffectsArrange<BubbleRowPanel>(AlignRightProperty);
    }

    public double MaxBubbleWidth
    {
        get => GetValue(MaxBubbleWidthProperty);
        set => SetValue(MaxBubbleWidthProperty, value);
    }

    public double MaxFraction
    {
        get => GetValue(MaxFractionProperty);
        set => SetValue(MaxFractionProperty, value);
    }

    public bool AlignRight
    {
        get => GetValue(AlignRightProperty);
        set => SetValue(AlignRightProperty, value);
    }

    private double Limit(double width) =>
        double.IsInfinity(width) ? MaxBubbleWidth : Math.Min(MaxBubbleWidth, width * MaxFraction);

    protected override Size MeasureOverride(Size availableSize)
    {
        var limit = Limit(availableSize.Width);
        var height = 0.0;
        var width = 0.0;
        foreach (var child in Children)
        {
            child.Measure(new Size(limit, availableSize.Height));
            width = Math.Max(width, child.DesiredSize.Width);
            height = Math.Max(height, child.DesiredSize.Height);
        }

        return new Size(double.IsInfinity(availableSize.Width) ? width : availableSize.Width, height);
    }

    protected override Size ArrangeOverride(Size finalSize)
    {
        foreach (var child in Children)
        {
            var w = Math.Min(child.DesiredSize.Width, Limit(finalSize.Width));
            var x = AlignRight || child.HorizontalAlignment == HorizontalAlignment.Right ? finalSize.Width - w : 0;
            child.Arrange(new Rect(x, 0, w, finalSize.Height));
        }

        return finalSize;
    }
}
