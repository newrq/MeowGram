using System.Diagnostics;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Documents;
using Avalonia.Input;
using Avalonia.Media;
using MeowGram.ViewModels.Messenger.Feed;

namespace MeowGram.Controls;

/// <summary>
/// Текст сообщения с форматированием TDLib (жирный, курсив, код, зачёркнутый, ссылки). Ссылки — цвет имён
/// (Mg.NameBrush) с подчёркиванием; клик открывает их в браузере. Выделять и копировать текст можно (SelectableTextBlock).
/// </summary>
public sealed class RichMessageText : SelectableTextBlock
{
    public static readonly StyledProperty<IReadOnlyList<RichSegment>?> SegmentsProperty =
        AvaloniaProperty.Register<RichMessageText, IReadOnlyList<RichSegment>?>(nameof(Segments));

    public static readonly StyledProperty<IBrush?> LinkBrushProperty =
        AvaloniaProperty.Register<RichMessageText, IBrush?>(nameof(LinkBrush));

    private readonly List<(int Start, int End, string Url)> _links = [];
    private Point _pressPoint;

    public IReadOnlyList<RichSegment>? Segments
    {
        get => GetValue(SegmentsProperty);
        set => SetValue(SegmentsProperty, value);
    }

    public IBrush? LinkBrush
    {
        get => GetValue(LinkBrushProperty);
        set => SetValue(LinkBrushProperty, value);
    }

    protected override Type StyleKeyOverride => typeof(SelectableTextBlock);

    protected override void OnPropertyChanged(AvaloniaPropertyChangedEventArgs change)
    {
        base.OnPropertyChanged(change);
        if (change.Property == SegmentsProperty || change.Property == LinkBrushProperty)
            Rebuild();
    }

    private void Rebuild()
    {
        _links.Clear();
        var inlines = new InlineCollection();
        var position = 0;
        foreach (var segment in Segments ?? [])
        {
            var run = new Run(segment.Text);
            if (segment.Bold)
                run.FontWeight = FontWeight.SemiBold;
            if (segment.Italic)
                run.FontStyle = FontStyle.Italic;
            if (segment.Code)
                run.FontFamily = new FontFamily("Cascadia Mono, Consolas, Menlo, monospace");

            var decorations = new TextDecorationCollection();
            if (segment.Strike)
                decorations.AddRange(Avalonia.Media.TextDecorations.Strikethrough);
            if (segment.Underline || segment.IsLink)
                decorations.AddRange(Avalonia.Media.TextDecorations.Underline);
            if (decorations.Count > 0)
                run.TextDecorations = decorations;

            if (segment.Url is { } url)
            {
                if (LinkBrush is { } brush)
                    run.Foreground = brush;
                _links.Add((position, position + segment.Text.Length, url));
            }

            inlines.Add(run);
            position += segment.Text.Length;
        }

        Inlines = inlines;
    }

    protected override void OnPointerMoved(PointerEventArgs e)
    {
        base.OnPointerMoved(e);
        Cursor = LinkAt(e.GetPosition(this)) is not null ? new Cursor(StandardCursorType.Hand) : new Cursor(StandardCursorType.Ibeam);
    }

    protected override void OnPointerPressed(PointerPressedEventArgs e)
    {
        _pressPoint = e.GetPosition(this);
        base.OnPointerPressed(e);
    }

    protected override void OnPointerReleased(PointerReleasedEventArgs e)
    {
        base.OnPointerReleased(e);
        var point = e.GetPosition(this);
        // Клик, а не выделение мышью.
        if (e.InitialPressMouseButton != MouseButton.Left || Math.Abs(point.X - _pressPoint.X) + Math.Abs(point.Y - _pressPoint.Y) > 4)
            return;

        if (LinkAt(point) is { } url)
        {
            Open(url);
            e.Handled = true;
        }
    }

    private string? LinkAt(Point point)
    {
        if (_links.Count == 0)
            return null;

        var hit = TextLayout.HitTestPoint(point - new Point(Padding.Left, Padding.Top));
        if (!hit.IsInside)
            return null;

        var index = hit.TextPosition;
        foreach (var (start, end, url) in _links)
        {
            if (index >= start && index < end)
                return url;
        }

        return null;
    }

    public static void Open(string url)
    {
        try
        {
            Process.Start(new ProcessStartInfo(url) { UseShellExecute = true });
        }
        catch (Exception ex) when (ex is System.ComponentModel.Win32Exception or InvalidOperationException)
        {
            Debug.WriteLine($"Не удалось открыть ссылку {url}: {ex.Message}");
        }
    }
}
