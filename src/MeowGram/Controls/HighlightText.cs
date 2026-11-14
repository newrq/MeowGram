using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Documents;
using Avalonia.Media;
using MeowGram.ViewModels.Messenger;

namespace MeowGram.Controls;

/// <summary>Однострочный текст поиска, где совпадения с запросом подсвечены фоном (mark в макете).</summary>
public sealed class HighlightText : TextBlock
{
    public static readonly StyledProperty<IReadOnlyList<TextSegment>?> SegmentsProperty =
        AvaloniaProperty.Register<HighlightText, IReadOnlyList<TextSegment>?>(nameof(Segments));

    public static readonly StyledProperty<IBrush?> MarkBrushProperty =
        AvaloniaProperty.Register<HighlightText, IBrush?>(nameof(MarkBrush));

    public IReadOnlyList<TextSegment>? Segments
    {
        get => GetValue(SegmentsProperty);
        set => SetValue(SegmentsProperty, value);
    }

    public IBrush? MarkBrush
    {
        get => GetValue(MarkBrushProperty);
        set => SetValue(MarkBrushProperty, value);
    }

    protected override Type StyleKeyOverride => typeof(TextBlock);

    protected override void OnPropertyChanged(AvaloniaPropertyChangedEventArgs change)
    {
        base.OnPropertyChanged(change);
        if (change.Property != SegmentsProperty && change.Property != MarkBrushProperty)
            return;

        var inlines = new InlineCollection();
        foreach (var segment in Segments ?? [])
        {
            var run = new Run(segment.Text);
            if (segment.IsMatch)
                run.Background = MarkBrush;
            inlines.Add(run);
        }

        Inlines = inlines;
    }
}
