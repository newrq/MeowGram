using Avalonia;
using Avalonia.Media;

namespace MeowGram.Controls;

/// <summary>
/// Для контролов, которые рисуют сами: перерисовать владельца, когда меняется свойство (цвет) у той же кисти.
/// Так бывает, пока тянут ползунок темы — ThemeService меняет цвет кистей, не подменяя их (см. ThemeService.PreviewBlend).
/// Подписка держится на кисти из ресурсов, поэтому при отсоединении от дерева вызывать <see cref="Clear"/>.
/// </summary>
internal sealed class BrushWatcher(Visual owner)
{
    private AvaloniaObject? _first;
    private AvaloniaObject? _second;
    private AvaloniaObject? _third;

    public void Watch(IBrush? first, IBrush? second = null, IBrush? third = null)
    {
        Swap(ref _first, first as AvaloniaObject);
        Swap(ref _second, second as AvaloniaObject);
        Swap(ref _third, third as AvaloniaObject);
    }

    public void Clear() => Watch(null);

    private void Swap(ref AvaloniaObject? slot, AvaloniaObject? brush)
    {
        if (ReferenceEquals(slot, brush))
            return;

        if (slot is not null)
            slot.PropertyChanged -= OnBrushChanged;
        slot = brush;
        if (slot is not null)
            slot.PropertyChanged += OnBrushChanged;
    }

    private void OnBrushChanged(object? sender, AvaloniaPropertyChangedEventArgs e) => owner.InvalidateVisual();
}
