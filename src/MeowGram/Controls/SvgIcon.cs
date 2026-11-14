using System.Globalization;
using System.Text.RegularExpressions;
using System.Xml;
using System.Xml.Linq;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Documents;
using Avalonia.Media;
using Avalonia.Platform;

namespace MeowGram.Controls;

/// <summary>
/// Иконка из SVG-файла в ресурсах (Assets/icons). Замена файла в Assets/icons меняет иконку без правки кода.
/// Все цвета SVG заменяются на <see cref="Foreground"/> (по умолчанию наследуется от кнопки/текста вокруг):
/// иконка одноцветная, важно только, есть ли заливка и обводка (<c>fill="none"</c> / <c>stroke="none"</c> — нет).
/// Поддерживается:
/// элементы <c>path</c>, <c>circle</c>, <c>ellipse</c>, <c>rect</c> (с <c>rx</c>/<c>ry</c>), <c>line</c>, <c>polyline</c>, <c>polygon</c>;
/// группы <c>g</c> с наследованием атрибутов и <c>transform</c>;
/// <c>fill</c>, <c>fill-rule</c>, <c>stroke</c>, <c>stroke-width</c>, <c>stroke-linecap</c>, <c>stroke-linejoin</c>,
/// <c>stroke-miterlimit</c>, <c>opacity</c>, <c>fill-opacity</c>, <c>stroke-opacity</c> — атрибутами или в <c>style</c>.
/// Как в SVG: заливка по умолчанию есть, обводки нет, правило заливки nonzero. Содержимое <c>defs</c>, <c>clipPath</c>,
/// <c>mask</c> и т. п. не рисуется; градиенты, <c>use</c>, текст и пунктир не поддерживаются.
/// </summary>
public sealed partial class SvgIcon : Control
{
    public static readonly StyledProperty<string?> SourceProperty =
        AvaloniaProperty.Register<SvgIcon, string?>(nameof(Source));

    public static readonly StyledProperty<IBrush?> ForegroundProperty =
        TextElement.ForegroundProperty.AddOwner<SvgIcon>();

    private static readonly Dictionary<string, SvgShape?> Cache = [];

    private SvgShape? _shape;

    static SvgIcon()
    {
        AffectsRender<SvgIcon>(ForegroundProperty);
        WidthProperty.OverrideDefaultValue<SvgIcon>(20);
        HeightProperty.OverrideDefaultValue<SvgIcon>(20);
    }

    /// <summary>Путь к SVG: avares://MeowGram/Assets/icons/moon.svg.</summary>
    public string? Source
    {
        get => GetValue(SourceProperty);
        set => SetValue(SourceProperty, value);
    }

    public IBrush? Foreground
    {
        get => GetValue(ForegroundProperty);
        set => SetValue(ForegroundProperty, value);
    }

    protected override void OnPropertyChanged(AvaloniaPropertyChangedEventArgs change)
    {
        base.OnPropertyChanged(change);
        if (change.Property == SourceProperty)
        {
            _shape = Source is { Length: > 0 } source ? Load(source) : null;
            InvalidateVisual();
        }
    }

    public override void Render(DrawingContext context)
    {
        if (_shape is not { } shape || Foreground is not { } brush)
            return;

        var box = shape.ViewBox;
        var scale = Math.Min(Bounds.Width / box.Width, Bounds.Height / box.Height);
        var offsetX = (Bounds.Width - box.Width * scale) / 2 - box.X * scale;
        var offsetY = (Bounds.Height - box.Height * scale) / 2 - box.Y * scale;
        var viewBox = Matrix.CreateScale(scale, scale) * Matrix.CreateTranslation(offsetX, offsetY);

        foreach (var layer in shape.Layers)
        {
            // Преобразование слоя применяется до viewBox; обводка масштабируется вместе с фигурой, как в SVG.
            using (context.PushTransform(layer.Transform * viewBox))
                layer.Draw(context, brush);
        }
    }

    private static SvgShape? Load(string source)
    {
        if (Cache.TryGetValue(source, out var cached))
            return cached;

        SvgShape? shape = null;
        try
        {
            using var stream = AssetLoader.Open(new Uri(source));
            // DOCTYPE из экспорта редакторов пропускается, внешние сущности не загружаются.
            using var reader = XmlReader.Create(stream, new XmlReaderSettings { DtdProcessing = DtdProcessing.Ignore, XmlResolver = null });
            shape = Parse(XDocument.Load(reader));
        }
        catch (Exception ex) when (ex is IOException or InvalidDataException or UriFormatException
                                       or XmlException or FormatException or InvalidOperationException)
        {
            // Битый или отсутствующий файл — пустая иконка вместо падения экрана.
            System.Diagnostics.Debug.WriteLine($"SvgIcon: не удалось загрузить {source}: {ex.Message}");
        }

        Cache[source] = shape;
        return shape;
    }

    private static SvgShape? Parse(XDocument document)
    {
        if (document.Root is not { } root)
            return null;

        var layers = new List<SvgLayer>();
        Collect(root, SvgPaint.Default, layers);
        return layers.Count == 0 ? null : new SvgShape(ParseViewBox(root), layers);
    }

    /// <summary>Элементы, содержимое которых не рисуется само по себе.</summary>
    private static readonly HashSet<string> NonRendered =
    [
        "defs", "clipPath", "mask", "symbol", "marker", "pattern", "linearGradient", "radialGradient", "filter",
        "style", "title", "desc", "metadata", "text", "use", "image", "foreignObject",
    ];

    private static void Collect(XElement element, SvgPaint inherited, List<SvgLayer> layers)
    {
        var attributes = SvgAttributes.Of(element);
        var paint = inherited.With(attributes);
        if (paint.Hidden)
            return;

        if (BuildGeometry(element.Name.LocalName, attributes, paint.EvenOdd) is { } geometry)
            layers.Add(new SvgLayer(geometry, paint));

        foreach (var child in element.Elements())
        {
            if (!NonRendered.Contains(child.Name.LocalName))
                Collect(child, paint, layers);
        }
    }

    private static Geometry? BuildGeometry(string name, SvgAttributes a, bool evenOdd)
    {
        var data = name switch
        {
            "path" => a["d"],
            "circle" => EllipseData(a.Number("cx"), a.Number("cy"), a.Number("r"), a.Number("r")),
            "ellipse" => EllipseData(a.Number("cx"), a.Number("cy"), a.Number("rx"), a.Number("ry")),
            "rect" => RectData(a),
            "line" => Invariant($"M{a.Number("x1")},{a.Number("y1")} L{a.Number("x2")},{a.Number("y2")}"),
            "polyline" => PolyData(a["points"], close: false),
            "polygon" => PolyData(a["points"], close: true),
            _ => null,
        };

        if (string.IsNullOrWhiteSpace(data))
            return null;

        // Префикс F1 — nonzero (как по умолчанию в SVG), F0 — evenodd.
        return Geometry.Parse((evenOdd ? "F0 " : "F1 ") + data);
    }

    private static string? EllipseData(double cx, double cy, double rx, double ry)
    {
        if (rx <= 0 || ry <= 0)
            return null;

        return Invariant($"M{cx - rx},{cy} A{rx},{ry} 0 1 0 {cx + rx},{cy} A{rx},{ry} 0 1 0 {cx - rx},{cy} Z");
    }

    private static string? RectData(SvgAttributes a)
    {
        double x = a.Number("x"), y = a.Number("y"), w = a.Number("width"), h = a.Number("height");
        if (w <= 0 || h <= 0)
            return null;

        // Как в SVG: если задан только один радиус, второй равен ему; радиус не больше половины стороны.
        double? rxAttr = a.OptionalNumber("rx"), ryAttr = a.OptionalNumber("ry");
        var rx = Math.Clamp(rxAttr ?? ryAttr ?? 0, 0, w / 2);
        var ry = Math.Clamp(ryAttr ?? rxAttr ?? 0, 0, h / 2);
        if (rx <= 0 || ry <= 0)
            return Invariant($"M{x},{y} H{x + w} V{y + h} H{x} Z");

        return Invariant($"M{x + rx},{y} H{x + w - rx} A{rx},{ry} 0 0 1 {x + w},{y + ry} V{y + h - ry} ")
               + Invariant($"A{rx},{ry} 0 0 1 {x + w - rx},{y + h} H{x + rx} A{rx},{ry} 0 0 1 {x},{y + h - ry} ")
               + Invariant($"V{y + ry} A{rx},{ry} 0 0 1 {x + rx},{y} Z");
    }

    private static string? PolyData(string? points, bool close)
    {
        var values = SvgAttributes.Numbers(points);
        if (values.Count < 4)
            return null;

        var builder = new System.Text.StringBuilder();
        for (var i = 0; i + 1 < values.Count; i += 2)
            builder.Append(Invariant($"{(i == 0 ? "M" : " L")}{values[i]},{values[i + 1]}"));
        if (close)
            builder.Append(" Z");
        return builder.ToString();
    }

    private static string Invariant(FormattableString text) => text.ToString(CultureInfo.InvariantCulture);

    private static Rect ParseViewBox(XElement root)
    {
        var parts = SvgAttributes.Numbers((string?)root.Attribute("viewBox"));
        if (parts is { Count: 4 } && parts[2] > 0 && parts[3] > 0)
            return new Rect(parts[0], parts[1], parts[2], parts[3]);

        var width = double.TryParse((string?)root.Attribute("width"), CultureInfo.InvariantCulture, out var w) ? w : 24;
        var height = double.TryParse((string?)root.Attribute("height"), CultureInfo.InvariantCulture, out var h) ? h : 24;
        return new Rect(0, 0, width, height);
    }

    private sealed record SvgShape(Rect ViewBox, IReadOnlyList<SvgLayer> Layers);

    /// <summary>Одна фигура SVG с итоговыми (унаследованными) настройками заливки и обводки.</summary>
    private sealed class SvgLayer(Geometry geometry, SvgPaint paint)
    {
        public Matrix Transform => paint.Transform;

        public void Draw(DrawingContext context, IBrush brush)
        {
            var fill = paint.Fill ? brush : null;
            var pen = paint.Stroke && paint.StrokeWidth > 0
                ? new Pen(brush, paint.StrokeWidth, lineCap: paint.LineCap, lineJoin: paint.LineJoin, miterLimit: paint.MiterLimit)
                : null;

            var fillOpacity = paint.Opacity * paint.FillOpacity;
            var strokeOpacity = paint.Opacity * paint.StrokeOpacity;
            if (fill is not null && pen is not null && fillOpacity != strokeOpacity)
            {
                DrawWithOpacity(context, fill, null, fillOpacity);
                DrawWithOpacity(context, null, pen, strokeOpacity);
                return;
            }

            DrawWithOpacity(context, fill, pen, fill is not null ? fillOpacity : strokeOpacity);
        }

        private void DrawWithOpacity(DrawingContext context, IBrush? fill, IPen? pen, double opacity)
        {
            if ((fill is null && pen is null) || opacity <= 0)
                return;

            if (opacity >= 1)
            {
                context.DrawGeometry(fill, pen, geometry);
                return;
            }

            using (context.PushOpacity(opacity))
                context.DrawGeometry(fill, pen, geometry);
        }
    }

    /// <summary>Наследуемые настройки рисования (как свойства представления SVG).</summary>
    private readonly record struct SvgPaint(
        bool Fill,
        bool Stroke,
        double StrokeWidth,
        PenLineCap LineCap,
        PenLineJoin LineJoin,
        double MiterLimit,
        bool EvenOdd,
        double Opacity,
        double FillOpacity,
        double StrokeOpacity,
        Matrix Transform,
        bool Hidden)
    {
        public static SvgPaint Default { get; } =
            new(true, false, 1, PenLineCap.Flat, PenLineJoin.Miter, 4, false, 1, 1, 1, Matrix.Identity, false);

        public SvgPaint With(SvgAttributes a) => this with
        {
            Fill = ParsePaint(a["fill"], Fill),
            Stroke = ParsePaint(a["stroke"], Stroke),
            StrokeWidth = a.OptionalNumber("stroke-width") ?? StrokeWidth,
            LineCap = a["stroke-linecap"] switch
            {
                "round" => PenLineCap.Round,
                "square" => PenLineCap.Square,
                "butt" => PenLineCap.Flat,
                _ => LineCap,
            },
            LineJoin = a["stroke-linejoin"] switch
            {
                "round" => PenLineJoin.Round,
                "bevel" => PenLineJoin.Bevel,
                "miter" or "miter-clip" or "arcs" => PenLineJoin.Miter,
                _ => LineJoin,
            },
            MiterLimit = a.OptionalNumber("stroke-miterlimit") ?? MiterLimit,
            EvenOdd = a["fill-rule"] switch
            {
                "evenodd" => true,
                "nonzero" => false,
                _ => EvenOdd,
            },
            // opacity в SVG не наследуется, а применяется к группе целиком — для одноцветной иконки это то же умножение.
            Opacity = Opacity * Math.Clamp(a.OptionalNumber("opacity") ?? 1, 0, 1),
            FillOpacity = Math.Clamp(a.OptionalNumber("fill-opacity") ?? FillOpacity, 0, 1),
            StrokeOpacity = Math.Clamp(a.OptionalNumber("stroke-opacity") ?? StrokeOpacity, 0, 1),
            Transform = ParseTransform(a["transform"]) * Transform,
            Hidden = Hidden || a["display"] == "none" || a["visibility"] is "hidden" or "collapse",
        };

        /// <summary>Любой цвет (currentColor, #hex, url(...)) — значит «есть», цвет всё равно берётся из Foreground.</summary>
        private static bool ParsePaint(string? value, bool inherited) => value switch
        {
            null or "" or "inherit" => inherited,
            "none" or "transparent" => false,
            _ => true,
        };

        /// <summary>Список преобразований SVG: правое применяется первым.</summary>
        private static Matrix ParseTransform(string? value)
        {
            if (string.IsNullOrWhiteSpace(value))
                return Matrix.Identity;

            var result = Matrix.Identity;
            foreach (Match match in TransformPattern().Matches(value))
            {
                var args = SvgAttributes.Numbers(match.Groups[2].Value);
                var arg = (int i, double fallback) => i < args.Count ? args[i] : fallback;
                var step = match.Groups[1].Value switch
                {
                    "matrix" when args.Count == 6 => new Matrix(args[0], args[1], args[2], args[3], args[4], args[5]),
                    "translate" => Matrix.CreateTranslation(arg(0, 0), arg(1, 0)),
                    "scale" => Matrix.CreateScale(arg(0, 1), arg(1, arg(0, 1))),
                    "rotate" => Matrix.CreateTranslation(-arg(1, 0), -arg(2, 0))
                                * Matrix.CreateRotation(arg(0, 0) * Math.PI / 180)
                                * Matrix.CreateTranslation(arg(1, 0), arg(2, 0)),
                    "skewX" => Matrix.CreateSkew(arg(0, 0) * Math.PI / 180, 0),
                    "skewY" => Matrix.CreateSkew(0, arg(0, 0) * Math.PI / 180),
                    _ => Matrix.Identity,
                };
                result = step * result;
            }

            return result;
        }
    }

    /// <summary>Атрибуты элемента; объявления из <c>style</c> важнее одноимённых атрибутов, как в CSS.</summary>
    private sealed class SvgAttributes
    {
        private readonly XElement _element;
        private readonly Dictionary<string, string>? _style;

        private SvgAttributes(XElement element, Dictionary<string, string>? style)
        {
            _element = element;
            _style = style;
        }

        public string? this[string name] =>
            _style is not null && _style.TryGetValue(name, out var value) ? value : ((string?)_element.Attribute(name))?.Trim();

        public static SvgAttributes Of(XElement element)
        {
            Dictionary<string, string>? style = null;
            if ((string?)element.Attribute("style") is { Length: > 0 } css)
            {
                foreach (var declaration in css.Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
                {
                    var colon = declaration.IndexOf(':');
                    if (colon <= 0)
                        continue;
                    style ??= [];
                    style[declaration[..colon].Trim()] = declaration[(colon + 1)..].Trim();
                }
            }

            return new SvgAttributes(element, style);
        }

        public double Number(string name) => OptionalNumber(name) ?? 0;

        public double? OptionalNumber(string name)
        {
            var value = this[name];
            if (string.IsNullOrEmpty(value))
                return null;
            if (value.EndsWith("px", StringComparison.Ordinal))
                value = value[..^2];
            return double.TryParse(value, NumberStyles.Float, CultureInfo.InvariantCulture, out var number) ? number : null;
        }

        /// <summary>Числа через пробелы и/или запятые, в том числе слитные: «1-2», «.5.5».</summary>
        public static List<double> Numbers(string? text)
        {
            var result = new List<double>();
            if (string.IsNullOrWhiteSpace(text))
                return result;

            foreach (Match match in NumberPattern().Matches(text))
            {
                if (double.TryParse(match.Value, NumberStyles.Float, CultureInfo.InvariantCulture, out var number))
                    result.Add(number);
            }

            return result;
        }
    }

    [GeneratedRegex(@"(matrix|translate|scale|rotate|skewX|skewY)\s*\(([^)]*)\)")]
    private static partial Regex TransformPattern();

    [GeneratedRegex(@"[-+]?(?:\d+\.?\d*|\.\d+)(?:[eE][-+]?\d+)?")]
    private static partial Regex NumberPattern();
}
