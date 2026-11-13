using System.Collections;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Media;
using MeowGram.Animation;
using MeowGram.Services;
using QRCoder;

namespace MeowGram.Controls;

/// <summary>
/// QR-код входа. Тёмные точки на светлой «бумаге» (в обеих темах — так его сканирует любой телефон).
/// Анимации:
/// появление — точки прорастают волной от трёх угловых маркеров;
/// новая ссылка — волна перекрашивания слева направо;
/// <see cref="IsTorn"/> — «бумага» рвётся по зубчатой линии, половинки разъезжаются и наклоняются, в щели — телефон;
/// <see cref="IsSeparated"/> — половинки улетают за края (переход ко входу по номеру).
/// Пока <see cref="Link"/> пуст — «бумага» мягко пульсирует (ждём ссылку от TDLib).
/// </summary>
public sealed class QrCodeView : Control
{
    public static readonly StyledProperty<string?> LinkProperty =
        AvaloniaProperty.Register<QrCodeView, string?>(nameof(Link));

    public static readonly StyledProperty<bool> IsTornProperty =
        AvaloniaProperty.Register<QrCodeView, bool>(nameof(IsTorn));

    public static readonly StyledProperty<bool> IsSeparatedProperty =
        AvaloniaProperty.Register<QrCodeView, bool>(nameof(IsSeparated));

    public static readonly StyledProperty<IBrush?> PaperBrushProperty =
        AvaloniaProperty.Register<QrCodeView, IBrush?>(nameof(PaperBrush), new SolidColorBrush(Color.Parse("#E8E8E8")));

    public static readonly StyledProperty<IBrush?> InkBrushProperty =
        AvaloniaProperty.Register<QrCodeView, IBrush?>(nameof(InkBrush), new SolidColorBrush(Color.Parse("#030303")));

    public static readonly StyledProperty<IBrush?> IconBrushProperty =
        AvaloniaProperty.Register<QrCodeView, IBrush?>(nameof(IconBrush), Brushes.White);

    private static readonly Geometry PhoneIcon =
        StreamGeometry.Parse("M7 2h10a2 2 0 0 1 2 2v16a2 2 0 0 1-2 2H7a2 2 0 0 1-2-2V4a2 2 0 0 1 2-2z M12 18h.01");

    private readonly Tween _tear = new();
    private readonly Tween _separate = new();
    private readonly FrameClock _clock;
    private readonly double[] _fiberLengths;
    private QrMatrix? _matrix;
    private QrMatrix? _previous;
    private TimeSpan _appearAt;
    private TimeSpan _refreshAt;
    private bool _refreshing;

    static QrCodeView()
    {
        // Метаданные свойства задаются один раз на тип. В конструкторе экземпляра второй QR
        // (возврат на шаг QR) падал с «Metadata is already set» — и карточка оставалась пустой.
        WidthProperty.OverrideDefaultValue<QrCodeView>(208);
        HeightProperty.OverrideDefaultValue<QrCodeView>(208);
    }

    public QrCodeView()
    {
        _clock = new FrameClock(this, IsAnimating);
        var random = new Random(42);
        _fiberLengths = Enumerable.Range(0, 64).Select(_ => 1.5 + random.NextDouble() * 4).ToArray();
        ClipToBounds = false;
    }

    public string? Link
    {
        get => GetValue(LinkProperty);
        set => SetValue(LinkProperty, value);
    }

    public bool IsTorn
    {
        get => GetValue(IsTornProperty);
        set => SetValue(IsTornProperty, value);
    }

    public bool IsSeparated
    {
        get => GetValue(IsSeparatedProperty);
        set => SetValue(IsSeparatedProperty, value);
    }

    public IBrush? PaperBrush
    {
        get => GetValue(PaperBrushProperty);
        set => SetValue(PaperBrushProperty, value);
    }

    public IBrush? InkBrush
    {
        get => GetValue(InkBrushProperty);
        set => SetValue(InkBrushProperty, value);
    }

    /// <summary>Цвет телефона в щели разрыва (на фоне карточки).</summary>
    public IBrush? IconBrush
    {
        get => GetValue(IconBrushProperty);
        set => SetValue(IconBrushProperty, value);
    }

    /// <summary>Длительность появления: маркеры 220 мс, волна точек 450 мс + 250 мс на точку.</summary>
    private static TimeSpan AppearTotal => Motion.Ms(700);

    protected override void OnPropertyChanged(AvaloniaPropertyChangedEventArgs change)
    {
        base.OnPropertyChanged(change);
        var now = FrameClock.Now;

        if (change.Property == LinkProperty)
        {
            var next = string.IsNullOrEmpty(Link) ? null : QrMatrix.Create(Link!);
            if (next is not null && _matrix is not null && next.Size == _matrix.Size && !IsAppearing(now))
            {
                _previous = _matrix;
                _refreshAt = now;
                _refreshing = true;
            }
            else
            {
                _previous = null;
                _refreshing = false;
                _appearAt = now;
            }

            _matrix = next;
        }
        else if (change.Property == IsTornProperty)
        {
            // Рвётся упруго (с перелётом), склеивается быстрее и с коротким «щелчком».
            if (IsTorn)
                _tear.To(1, now, Motion.Ms(320), t => Ease.OutBack(t, 1.4));
            else
                _tear.To(0, now, Motion.Ms(260), t => Ease.OutBack(t, 1.1));
        }
        else if (change.Property == IsSeparatedProperty)
        {
            _separate.To(IsSeparated ? 1 : 0, now, Motion.Ms(450), Ease.InOutCubic);
        }
        else if (change.Property != PaperBrushProperty && change.Property != InkBrushProperty && change.Property != IconBrushProperty)
        {
            return;
        }

        _clock.Kick();
    }

    private bool IsAppearing(TimeSpan now) => _matrix is not null && now - _appearAt < AppearTotal;

    private bool IsAnimating()
    {
        var now = FrameClock.Now;
        return _matrix is null // пульсация ожидания
               || IsAppearing(now)
               || (_refreshing && now - _refreshAt < Motion.Ms(460))
               || _tear.IsRunning(now)
               || _separate.IsRunning(now);
    }

    public override void Render(DrawingContext context)
    {
        var now = FrameClock.Now;
        var paper = new Rect(Bounds.Size);
        var tear = _tear.Value(now);
        var separate = _separate.Value(now);

        if (separate >= 0.999)
            return;

        // В щели — телефон: подсказка, что за кнопкой вход по номеру.
        var iconOpacity = Ease.Clamp01(tear) * (1 - separate);
        if (iconOpacity > 0.01 && IconBrush is not null)
        {
            var scale = (0.8 + 0.2 * Ease.Clamp01(tear)) * 1.25;
            var center = paper.Center;
            using (context.PushOpacity(iconOpacity))
            using (context.PushTransform(Matrix.CreateTranslation(-12, -12) * Matrix.CreateScale(scale, scale) * Matrix.CreateTranslation(center.X, center.Y)))
                context.DrawGeometry(null, new Pen(IconBrush, 2, lineCap: PenLineCap.Round, lineJoin: PenLineJoin.Round), PhoneIcon);
        }

        if (tear <= 0.001 && separate <= 0.001)
        {
            DrawPaper(context, paper, now);
            return;
        }

        var (left, right, edge) = TearGeometry(paper);
        var pivot = new Point(paper.Center.X, paper.Bottom);
        DrawHalf(context, paper, left, edge, now, pivot, tear, separate, -1);
        DrawHalf(context, paper, right, edge, now, pivot, tear, separate, +1);
    }

    private void DrawHalf(DrawingContext context, Rect paper, Geometry clip, IReadOnlyList<Point> edge, TimeSpan now,
        Point pivot, double tear, double separate, int side)
    {
        var angle = side * (6 * tear + 14 * separate) * Math.PI / 180;
        var shift = side * (18 * tear + paper.Width * 0.9 * separate);
        var transform = Matrix.CreateTranslation(-pivot.X, -pivot.Y)
                        * Matrix.CreateRotation(angle)
                        * Matrix.CreateTranslation(pivot.X + shift, pivot.Y);

        using (context.PushOpacity(1 - separate))
        using (context.PushTransform(transform))
        {
            using (context.PushGeometryClip(clip))
                DrawPaper(context, paper, now);

            // «Волокна» по краю разрыва.
            if (PaperBrush is not null && tear > 0.05)
            {
                var pen = new Pen(PaperBrush, 1.1, lineCap: PenLineCap.Round);
                using (context.PushOpacity(Ease.Clamp01(tear)))
                {
                    for (var i = 1; i < edge.Count - 1; i++)
                    {
                        var length = _fiberLengths[(i * 7 + (side > 0 ? 3 : 0)) % _fiberLengths.Length] * Ease.Clamp01(tear);
                        var p = edge[i];
                        context.DrawLine(pen, p, new Point(p.X - side * length, p.Y + (i % 2 == 0 ? -1 : 1) * length * 0.4));
                    }
                }
            }
        }
    }

    /// <summary>Зубчатая линия разрыва по центру и две дополняющие друг друга половинки.</summary>
    private static (Geometry Left, Geometry Right, IReadOnlyList<Point> Edge) TearGeometry(Rect paper)
    {
        const int teeth = 14;
        var edge = new List<Point>(teeth + 1);
        for (var i = 0; i <= teeth; i++)
        {
            var y = paper.Top + paper.Height * i / teeth;
            var x = paper.Center.X + (i % 2 == 0 ? -1 : 1) * (i % 3 == 0 ? 5 : 7);
            edge.Add(new Point(x, y));
        }

        var margin = 40.0; // половинки могут наклоняться — клип с запасом
        var left = new StreamGeometry();
        using (var g = left.Open())
        {
            g.BeginFigure(new Point(paper.Left - margin, paper.Top - margin), true);
            g.LineTo(new Point(edge[0].X, paper.Top - margin));
            foreach (var p in edge)
                g.LineTo(p);
            g.LineTo(new Point(edge[^1].X, paper.Bottom + margin));
            g.LineTo(new Point(paper.Left - margin, paper.Bottom + margin));
            g.EndFigure(true);
        }

        var right = new StreamGeometry();
        using (var g = right.Open())
        {
            g.BeginFigure(new Point(paper.Right + margin, paper.Top - margin), true);
            g.LineTo(new Point(edge[0].X, paper.Top - margin));
            foreach (var p in edge)
                g.LineTo(p);
            g.LineTo(new Point(edge[^1].X, paper.Bottom + margin));
            g.LineTo(new Point(paper.Right + margin, paper.Bottom + margin));
            g.EndFigure(true);
        }

        return (left, right, edge);
    }

    private void DrawPaper(DrawingContext context, Rect paper, TimeSpan now)
    {
        if (_matrix is null)
        {
            // Ждём ссылку: «бумага» пульсирует.
            var pulse = Motion.IsOff ? 0.6 : 0.45 + 0.2 * (0.5 + 0.5 * Math.Sin(now.TotalSeconds * 3));
            using (context.PushOpacity(pulse))
                context.DrawRectangle(PaperBrush, null, new RoundedRect(paper, 20));
            return;
        }

        var paperIn = Ease.OutCubic(Ease.Progress(now, _appearAt, Motion.Ms(220)));
        using (context.PushOpacity(Math.Max(paperIn, _refreshing ? 1 : 0)))
            context.DrawRectangle(PaperBrush, null, new RoundedRect(paper, 20));

        var module = paper.Width / _matrix.Size;

        if (_refreshing && _previous is not null)
        {
            DrawRefresh(context, paper, module, now);
            return;
        }

        // Угловые маркеры — первыми.
        var finderScale = Ease.OutBack(Ease.Progress(now, _appearAt, Motion.Ms(220)));
        foreach (var finder in _matrix.Finders)
            DrawFinder(context, paper, module, finder, finderScale);

        // Точки — волной от маркеров.
        var appearMs = Motion.Ms(250);
        foreach (var (row, col, delay) in _matrix.Dots)
        {
            var start = _appearAt + Motion.Scale(TimeSpan.FromMilliseconds(120 + 450 * delay));
            var scale = Ease.OutBack(Ease.Progress(now, start, appearMs));
            if (scale > 0.01)
                DrawDot(context, paper, module, row, col, scale);
        }
    }

    private void DrawRefresh(DrawingContext context, Rect paper, double module, TimeSpan now)
    {
        foreach (var finder in _matrix!.Finders)
            DrawFinder(context, paper, module, finder, 1);

        var size = _matrix.Size;
        for (var row = 0; row < size; row++)
        {
            for (var col = 0; col < size; col++)
            {
                if (_matrix.IsFinder(row, col))
                    continue;

                var wasOn = _previous!.Get(row, col);
                var isOn = _matrix.Get(row, col);
                if (!wasOn && !isOn)
                    continue;

                if (wasOn && isOn)
                {
                    DrawDot(context, paper, module, row, col, 1);
                    continue;
                }

                var start = _refreshAt + Motion.Scale(TimeSpan.FromMilliseconds(300.0 * col / size));
                var t = Ease.Progress(now, start, Motion.Ms(160));
                DrawDot(context, paper, module, row, col, isOn ? Ease.OutBack(t) : 1 - Ease.OutCubic(t));
            }
        }
    }

    private void DrawDot(DrawingContext context, Rect paper, double module, int row, int col, double scale)
    {
        var size = module * 0.86 * scale;
        var cx = paper.X + (col + 0.5) * module;
        var cy = paper.Y + (row + 0.5) * module;
        context.DrawRectangle(InkBrush, null, new RoundedRect(new Rect(cx - size / 2, cy - size / 2, size, size), size * 0.35));
    }

    private void DrawFinder(DrawingContext context, Rect paper, double module, (int Row, int Col) finder, double scale)
    {
        if (scale <= 0.01)
            return;

        var outer = new Rect(paper.X + finder.Col * module, paper.Y + finder.Row * module, 7 * module, 7 * module);
        var center = outer.Center;
        using (context.PushTransform(Matrix.CreateTranslation(-center.X, -center.Y) * Matrix.CreateScale(scale, scale) * Matrix.CreateTranslation(center.X, center.Y)))
        {
            context.DrawRectangle(InkBrush, null, new RoundedRect(outer, module * 2));
            context.DrawRectangle(PaperBrush, null, new RoundedRect(outer.Deflate(module), module * 1.4));
            context.DrawRectangle(InkBrush, null, new RoundedRect(outer.Deflate(module * 2), module));
        }
    }

    /// <summary>Матрица QR (с тихой зоной) и заранее посчитанные задержки появления точек.</summary>
    private sealed class QrMatrix
    {
        private readonly List<BitArray> _rows;

        private QrMatrix(List<BitArray> rows)
        {
            _rows = rows;
            Size = rows.Count;
            const int quiet = 4;
            Finders = [(quiet, quiet), (quiet, Size - quiet - 7), (Size - quiet - 7, quiet)];

            var centers = Finders.Select(f => (Row: f.Row + 3.5, Col: f.Col + 3.5)).ToArray();
            var dots = new List<(int, int, double Distance)>();
            for (var row = 0; row < Size; row++)
            {
                for (var col = 0; col < Size; col++)
                {
                    if (!Get(row, col) || IsFinder(row, col))
                        continue;

                    var distance = centers.Min(c => Math.Sqrt(Math.Pow(row + 0.5 - c.Row, 2) + Math.Pow(col + 0.5 - c.Col, 2)));
                    dots.Add((row, col, distance));
                }
            }

            var max = dots.Count == 0 ? 1 : dots.Max(d => d.Distance);
            Dots = dots.Select(d => (d.Item1, d.Item2, d.Distance / max)).ToArray();
        }

        public int Size { get; }

        public (int Row, int Col)[] Finders { get; }

        /// <summary>Тёмные модули вне маркеров; Delay ∈ [0, 1] — доля волны.</summary>
        public (int Row, int Col, double Delay)[] Dots { get; }

        public static QrMatrix Create(string text)
        {
            using var generator = new QRCodeGenerator();
            using var data = generator.CreateQrCode(text, QRCodeGenerator.ECCLevel.M);
            return new QrMatrix(data.ModuleMatrix.Select(r => new BitArray(r)).ToList());
        }

        public bool Get(int row, int col) => _rows[row][col];

        public bool IsFinder(int row, int col) =>
            Finders.Any(f => row >= f.Row && row < f.Row + 7 && col >= f.Col && col < f.Col + 7);
    }
}
