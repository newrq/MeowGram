using System.Globalization;
using System.Xml.Linq;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Documents;
using Avalonia.Media;
using Avalonia.Platform;

namespace MeowGram.Controls;

/// <summary>
/// Иконка из SVG-файла в ресурсах (Assets/icons): берутся viewBox и все &lt;path d="…"&gt;, заливаются цветом
/// <see cref="Foreground"/> (по умолчанию наследуется от кнопки/текста вокруг). Правило заливки — nonzero, как в SVG,
/// если у пути не указан fill-rule="evenodd". Другие элементы SVG (круги, группы с трансформацией) не поддерживаются.
/// Замена файла в Assets/icons меняет иконку без правки кода.
/// </summary>
public sealed class SvgIcon : Control
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
        if (_shape is not { } shape || Foreground is null)
            return;

        var box = shape.ViewBox;
        var scale = Math.Min(Bounds.Width / box.Width, Bounds.Height / box.Height);
        var offsetX = (Bounds.Width - box.Width * scale) / 2 - box.X * scale;
        var offsetY = (Bounds.Height - box.Height * scale) / 2 - box.Y * scale;

        using (context.PushTransform(Matrix.CreateScale(scale, scale) * Matrix.CreateTranslation(offsetX, offsetY)))
            context.DrawGeometry(Foreground, null, shape.Geometry);
    }

    private static SvgShape? Load(string source)
    {
        if (Cache.TryGetValue(source, out var cached))
            return cached;

        SvgShape? shape = null;
        try
        {
            using var stream = AssetLoader.Open(new Uri(source));
            shape = Parse(XDocument.Load(stream));
        }
        catch (Exception ex) when (ex is IOException or InvalidDataException or UriFormatException
                                       or System.Xml.XmlException or FormatException or InvalidOperationException)
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

        var paths = root.Descendants().Where(e => e.Name.LocalName == "path").ToList();
        if (paths.Count == 0)
            return null;

        var geometries = new List<Geometry>();
        foreach (var path in paths)
        {
            if ((string?)path.Attribute("d") is not { Length: > 0 } data)
                continue;

            // Префикс F1 — nonzero (как по умолчанию в SVG), F0 — evenodd.
            var evenOdd = (string?)path.Attribute("fill-rule") == "evenodd";
            geometries.Add(Geometry.Parse((evenOdd ? "F0 " : "F1 ") + data));
        }

        Geometry? geometry = geometries.Count switch
        {
            0 => null,
            1 => geometries[0],
            _ => new GeometryGroup { FillRule = FillRule.NonZero, Children = new GeometryCollection(geometries) },
        };

        return geometry is null ? null : new SvgShape(ParseViewBox(root), geometry);
    }

    private static Rect ParseViewBox(XElement root)
    {
        var parts = ((string?)root.Attribute("viewBox"))?
            .Split([' ', ','], StringSplitOptions.RemoveEmptyEntries)
            .Select(p => double.Parse(p, CultureInfo.InvariantCulture))
            .ToArray();

        if (parts is { Length: 4 } && parts[2] > 0 && parts[3] > 0)
            return new Rect(parts[0], parts[1], parts[2], parts[3]);

        var width = double.TryParse((string?)root.Attribute("width"), CultureInfo.InvariantCulture, out var w) ? w : 24;
        var height = double.TryParse((string?)root.Attribute("height"), CultureInfo.InvariantCulture, out var h) ? h : 24;
        return new Rect(0, 0, width, height);
    }

    private sealed record SvgShape(Rect ViewBox, Geometry Geometry);
}
