using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Presenters;
using Avalonia.Controls.Primitives;
using Avalonia.Input;
using Avalonia.Media;
using Avalonia.Threading;
using MeowGram.Animation;
using MeowGram.Services;

namespace MeowGram.Controls;

/// <summary>
/// Поле ввода MeowGram: каждый введённый символ падает сверху, удалённый — проваливается вниз.
/// <see cref="IsPrivate"/> — приватное поле: падают «*», справа «глазик» переворачивает символы в буквы и обратно.
/// <see cref="HasError"/> — красная граница и короткое покачивание.
/// Ввод, выделение, IME и буфер обмена — стандартные (прозрачный TextPresenter), символы рисует <see cref="GlyphLayer"/>.
/// </summary>
public class MeowTextBox : TextBox
{
    public static readonly StyledProperty<bool> IsPrivateProperty =
        AvaloniaProperty.Register<MeowTextBox, bool>(nameof(IsPrivate));

    public static readonly StyledProperty<bool> HasErrorProperty =
        AvaloniaProperty.Register<MeowTextBox, bool>(nameof(HasError));

    private GlyphLayer? _layer;

    protected override Type StyleKeyOverride => typeof(MeowTextBox);

    public bool IsPrivate
    {
        get => GetValue(IsPrivateProperty);
        set => SetValue(IsPrivateProperty, value);
    }

    public bool HasError
    {
        get => GetValue(HasErrorProperty);
        set => SetValue(HasErrorProperty, value);
    }

    /// <summary>Покачать поле (например, при повторной ошибке, когда HasError уже true).</summary>
    public void Shake() => ShakeAnimation.Run(this);

    protected override void OnApplyTemplate(TemplateAppliedEventArgs e)
    {
        base.OnApplyTemplate(e);
        _layer = e.NameScope.Find<GlyphLayer>("PART_GlyphLayer");
        _layer?.Attach(this, e.NameScope.Find<TextPresenter>("PART_TextPresenter"));
    }

    protected override void OnPropertyChanged(AvaloniaPropertyChangedEventArgs change)
    {
        base.OnPropertyChanged(change);

        if (change.Property == IsPrivateProperty)
        {
            PasswordChar = IsPrivate ? '*' : default;
            RevealPassword = false;
            PseudoClasses.Set(":private", IsPrivate);
        }
        else if (change.Property == HasErrorProperty)
        {
            PseudoClasses.Set(":error", HasError);
            if (HasError)
                Shake();
        }
        else if (change.Property == TextProperty)
        {
            _layer?.OnTextChanged(Text ?? string.Empty);
        }
        else if (change.Property == RevealPasswordProperty)
        {
            _layer?.OnRevealChanged();
        }
        else if (change.Property == CaretIndexProperty || change.Property == SelectionStartProperty || change.Property == SelectionEndProperty)
        {
            _layer?.OnCaretMoved();
        }
    }

    protected override void OnGotFocus(FocusChangedEventArgs e)
    {
        base.OnGotFocus(e);
        _layer?.OnFocusChanged(true);
    }

    protected override void OnLostFocus(FocusChangedEventArgs e)
    {
        base.OnLostFocus(e);
        _layer?.OnFocusChanged(false);
    }
}

/// <summary>
/// Слой над прозрачным TextPresenter: рисует символы с анимацией и свою плавную каретку.
/// Позиции символов берёт из раскладки TextPresenter, поэтому выделение и клики совпадают с нарисованным.
/// </summary>
public sealed class GlyphLayer : Control
{
    private readonly List<Glyph> _glyphs = [];
    private readonly List<Glyph> _dying = [];
    private readonly Dictionary<char, FormattedText> _cache = [];
    private readonly Random _random = new();
    private readonly Tween _caretX = new();
    private readonly FrameClock _clock;
    private readonly DispatcherTimer _blink;
    private readonly BrushWatcher _brushes;
    private MeowTextBox? _box;
    private TextPresenter? _presenter;
    private string _text = string.Empty;
    private bool _focused;
    private bool _caretVisible = true;
    private bool _caretPositioned;
    private TimeSpan _caretMovedAt;
    private (IBrush? Brush, double Size, Typeface Face) _cacheKey;

    public GlyphLayer()
    {
        _brushes = new BrushWatcher(this);
        IsHitTestVisible = false;
        _clock = new FrameClock(this, IsAnimating);
        _blink = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(530) };
        _blink.Tick += (_, _) =>
        {
            _caretVisible = FrameClock.Now - _caretMovedAt < TimeSpan.FromMilliseconds(530) || !_caretVisible;
            InvalidateVisual();
        };
    }

    internal void Attach(MeowTextBox box, TextPresenter? presenter)
    {
        _box = box;
        _presenter = presenter;
        _text = box.Text ?? string.Empty;
        _glyphs.Clear();
        _dying.Clear();
        foreach (var ch in _text)
            _glyphs.Add(new Glyph(ch, TimeSpan.Zero, 0));

        if (presenter is not null)
            presenter.LayoutUpdated += (_, _) => InvalidateVisual();
    }

    internal void OnTextChanged(string text)
    {
        var now = FrameClock.Now;
        var (start, removed, inserted) = GlyphMotion.Diff(_text, text);

        for (var i = 0; i < removed; i++)
        {
            var glyph = _glyphs[start];
            _glyphs.RemoveAt(start);
            glyph.DiedAt = now;
            _dying.Add(glyph);
        }

        var step = GlyphMotion.StaggerStep(inserted);
        for (var i = 0; i < inserted; i++)
            _glyphs.Insert(start + i, new Glyph(text[start + i], now + step * i, (_random.NextDouble() * 2 - 1) * 8));

        _text = text;
        OnCaretMoved();
        _clock.Kick();
    }

    internal void OnRevealChanged()
    {
        var now = FrameClock.Now;
        for (var i = 0; i < _glyphs.Count; i++)
            _glyphs[i].FlipAt = now + Motion.Ms(20 * i);
        _clock.Kick();
    }

    internal void OnCaretMoved()
    {
        _caretMovedAt = FrameClock.Now;
        _caretVisible = true;
        _clock.Kick();
    }

    internal void OnFocusChanged(bool focused)
    {
        _focused = focused;
        _caretVisible = true;
        _caretMovedAt = FrameClock.Now;
        if (focused)
            _blink.Start();
        else
            _blink.Stop();
        InvalidateVisual();
    }

    private bool IsAnimating()
    {
        var now = FrameClock.Now;
        return _dying.Count > 0
               || _caretX.IsRunning(now)
               || _glyphs.Exists(g => now - g.BornAt < GlyphMotion.AppearDuration
                                      || (g.FlipAt is { } f && now - f < GlyphMotion.FlipDuration)
                                      || g.X.IsRunning(now));
    }

    public override void Render(DrawingContext context)
    {
        if (_box is null || _presenter?.TextLayout is not { } layout)
            return;

        var now = FrameClock.Now;
        var origin = _presenter.TranslatePoint(default, this) ?? default;
        var fontSize = _box.FontSize;
        var masked = _box.PasswordChar != default && !_box.RevealPassword;
        EnsureCache();

        for (var i = 0; i < _glyphs.Count; i++)
        {
            var glyph = _glyphs[i];
            if (now < glyph.BornAt)
                continue;

            var box = layout.HitTestTextPosition(i);
            if (!glyph.Positioned)
            {
                glyph.X.Snap(box.X);
                glyph.Positioned = true;
            }
            else
            {
                glyph.X.To(box.X, now, Motion.Ms(120));
            }

            glyph.LastY = box.Y;
            var x = glyph.X.Value(now);

            var (offsetY, tilt, opacity) = GlyphMotion.Appear(Ease.Progress(now, glyph.BornAt, GlyphMotion.AppearDuration), glyph.Tilt);

            var showMasked = masked;
            var scaleY = 1.0;
            if (glyph.FlipAt is { } flipAt && now >= flipAt)
            {
                var (s, showNew) = GlyphMotion.Flip(Ease.Progress(now, flipAt, GlyphMotion.FlipDuration));
                scaleY = s;
                showMasked = showNew ? masked : !masked;
            }

            DrawGlyph(context, showMasked ? _box.PasswordChar : glyph.Char, origin + new Point(x, box.Y), offsetY * fontSize, tilt, scaleY, opacity);
        }

        for (var i = _dying.Count - 1; i >= 0; i--)
        {
            var glyph = _dying[i];
            var t = Ease.Progress(now, glyph.DiedAt, GlyphMotion.DisappearDuration);
            if (t >= 1)
            {
                _dying.RemoveAt(i);
                continue;
            }

            var (offsetY, opacity) = GlyphMotion.Disappear(t);
            DrawGlyph(context, masked ? _box.PasswordChar : glyph.Char, origin + new Point(glyph.X.Value(now), glyph.LastY), offsetY * fontSize, 0, 1, opacity);
        }

        DrawCaret(context, layout, origin, now);
    }

    private void DrawCaret(DrawingContext context, Avalonia.Media.TextFormatting.TextLayout layout, Point origin, TimeSpan now)
    {
        if (!_focused || _box is null || _box.SelectionStart != _box.SelectionEnd)
            return;

        var caret = layout.HitTestTextPosition(Math.Clamp(_box.CaretIndex, 0, _text.Length));
        if (!_caretPositioned)
        {
            _caretX.Snap(caret.X);
            _caretPositioned = true;
        }
        else
        {
            _caretX.To(caret.X, now, Motion.Ms(80));
        }

        if (!_caretVisible)
            return;

        var height = Math.Max(caret.Height, _box.FontSize * 1.2) * 0.8;
        var top = caret.Y + (caret.Height - height) / 2;
        var rect = new Rect(origin.X + _caretX.Value(now) - 0.75, origin.Y + top, 1.5, height);
        context.FillRectangle(_box.Foreground ?? Brushes.White, rect, 0.75f);
    }

    private void DrawGlyph(DrawingContext context, char ch, Point topLeft, double offsetY, double tiltDegrees, double scaleY, double opacity)
    {
        if (opacity <= 0.001)
            return;

        var text = GetText(ch);
        var center = new Point(topLeft.X + text.Width / 2, topLeft.Y + text.Height / 2);
        var matrix = Matrix.CreateTranslation(-center.X, -center.Y)
                     * Matrix.CreateScale(1, scaleY)
                     * Matrix.CreateRotation(tiltDegrees * Math.PI / 180)
                     * Matrix.CreateTranslation(center.X, center.Y + offsetY);

        using (context.PushOpacity(opacity))
        using (context.PushTransform(matrix))
            context.DrawText(text, topLeft);
    }

    protected override void OnDetachedFromVisualTree(VisualTreeAttachmentEventArgs e)
    {
        base.OnDetachedFromVisualTree(e);
        _brushes.Clear();
    }

    private void EnsureCache()
    {
        _brushes.Watch(_box!.Foreground);
        var key = (_box.Foreground, _box.FontSize, new Typeface(_box.FontFamily, _box.FontStyle, _box.FontWeight));
        if (key == _cacheKey)
            return;

        _cache.Clear();
        _cacheKey = key;
    }

    private FormattedText GetText(char ch)
    {
        if (!_cache.TryGetValue(ch, out var text))
        {
            text = new FormattedText(ch.ToString(), System.Globalization.CultureInfo.CurrentUICulture, FlowDirection.LeftToRight,
                _cacheKey.Face, _cacheKey.Size, _cacheKey.Brush ?? Brushes.White);
            _cache[ch] = text;
        }

        return text;
    }

    private sealed class Glyph(char ch, TimeSpan bornAt, double tilt)
    {
        public char Char { get; } = ch;

        public TimeSpan BornAt { get; } = bornAt;

        public double Tilt { get; } = tilt;

        public Tween X { get; } = new();

        public bool Positioned { get; set; }

        public double LastY { get; set; }

        public TimeSpan? FlipAt { get; set; }

        public TimeSpan DiedAt { get; set; }
    }
}
