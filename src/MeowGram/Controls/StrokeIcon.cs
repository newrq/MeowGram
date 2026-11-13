using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Documents;
using Avalonia.Media;

namespace MeowGram.Controls;

/// <summary>
/// Контурная иконка в сетке 24×24 (как у Lucide): масштабируется целиком, без искажения пропорций.
/// Цвет — <see cref="Foreground"/>, по умолчанию наследуется от кнопки/текста вокруг.
/// </summary>
public sealed class StrokeIcon : Control
{
    public static readonly StyledProperty<Geometry?> DataProperty =
        AvaloniaProperty.Register<StrokeIcon, Geometry?>(nameof(Data));

    public static readonly StyledProperty<IBrush?> ForegroundProperty =
        TextElement.ForegroundProperty.AddOwner<StrokeIcon>();

    public static readonly StyledProperty<double> StrokeThicknessProperty =
        AvaloniaProperty.Register<StrokeIcon, double>(nameof(StrokeThickness), 2.0);

    static StrokeIcon()
    {
        AffectsRender<StrokeIcon>(DataProperty, ForegroundProperty, StrokeThicknessProperty);
        WidthProperty.OverrideDefaultValue<StrokeIcon>(20);
        HeightProperty.OverrideDefaultValue<StrokeIcon>(20);
    }

    public Geometry? Data
    {
        get => GetValue(DataProperty);
        set => SetValue(DataProperty, value);
    }

    public IBrush? Foreground
    {
        get => GetValue(ForegroundProperty);
        set => SetValue(ForegroundProperty, value);
    }

    /// <summary>Толщина линии в единицах сетки 24×24.</summary>
    public double StrokeThickness
    {
        get => GetValue(StrokeThicknessProperty);
        set => SetValue(StrokeThicknessProperty, value);
    }

    public override void Render(DrawingContext context)
    {
        if (Data is null || Foreground is null)
            return;

        var scale = Math.Min(Bounds.Width, Bounds.Height) / 24.0;
        var offsetX = (Bounds.Width - 24 * scale) / 2;
        var offsetY = (Bounds.Height - 24 * scale) / 2;
        var pen = new Pen(Foreground, StrokeThickness, lineCap: PenLineCap.Round, lineJoin: PenLineJoin.Round);

        using (context.PushTransform(Matrix.CreateScale(scale, scale) * Matrix.CreateTranslation(offsetX, offsetY)))
            context.DrawGeometry(null, pen, Data);
    }
}
