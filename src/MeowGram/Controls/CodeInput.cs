using System.Windows.Input;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Input;
using Avalonia.Media;
using MeowGram.Animation;

namespace MeowGram.Controls;

/// <summary>
/// Код из Telegram/SMS: отдельные ячейки (их число — <see cref="CodeLength"/>, его сообщает TDLib), цифры падают в ячейки.
/// Принимает только цифры; когда введены все — <see cref="Completed"/> и <see cref="CompletedCommand"/> с кодом.
/// </summary>
public sealed class CodeInput : TextBox
{
    public const double CellWidth = 48;
    public const double CellGap = 10;

    public static readonly StyledProperty<int> CodeLengthProperty =
        AvaloniaProperty.Register<CodeInput, int>(nameof(CodeLength), 5, validate: v => v is > 0 and <= 12);

    public static readonly StyledProperty<bool> HasErrorProperty =
        AvaloniaProperty.Register<CodeInput, bool>(nameof(HasError));

    public static readonly StyledProperty<ICommand?> CompletedCommandProperty =
        AvaloniaProperty.Register<CodeInput, ICommand?>(nameof(CompletedCommand));

    private CodeCellsLayer? _cells;
    private bool _normalizing;

    public CodeInput()
    {
        MaxLength = CodeLength;
    }

    protected override Type StyleKeyOverride => typeof(CodeInput);

    public int CodeLength
    {
        get => GetValue(CodeLengthProperty);
        set => SetValue(CodeLengthProperty, value);
    }

    public bool HasError
    {
        get => GetValue(HasErrorProperty);
        set => SetValue(HasErrorProperty, value);
    }

    public ICommand? CompletedCommand
    {
        get => GetValue(CompletedCommandProperty);
        set => SetValue(CompletedCommandProperty, value);
    }

    /// <summary>Введены все цифры кода.</summary>
    public event EventHandler<string>? Completed;

    protected override void OnApplyTemplate(TemplateAppliedEventArgs e)
    {
        base.OnApplyTemplate(e);
        _cells = e.NameScope.Find<CodeCellsLayer>("PART_Cells");
        _cells?.Attach(this);
    }

    protected override Size MeasureOverride(Size availableSize)
    {
        base.MeasureOverride(availableSize);
        return new Size(CodeLength * CellWidth + (CodeLength - 1) * CellGap, Height is > 0 and var h ? h : 60);
    }

    protected override void OnTextInput(TextInputEventArgs e)
    {
        var digits = new string((e.Text ?? string.Empty).Where(char.IsAsciiDigit).ToArray());
        if (digits.Length == 0)
        {
            e.Handled = true;
            return;
        }

        e.Text = digits;
        base.OnTextInput(e);
    }

    protected override void OnPropertyChanged(AvaloniaPropertyChangedEventArgs change)
    {
        base.OnPropertyChanged(change);

        if (change.Property == CodeLengthProperty)
        {
            MaxLength = CodeLength;
            InvalidateMeasure();
            _cells?.InvalidateVisual();
        }
        else if (change.Property == TextProperty && !_normalizing)
        {
            OnTextChangedCore();
        }
        else if (change.Property == HasErrorProperty)
        {
            _cells?.InvalidateVisual();
            if (HasError)
                ShakeAnimation.Run(this);
        }
        else if (change.Property == CaretIndexProperty && CaretIndex != (Text?.Length ?? 0))
        {
            // Ячейки заполняются только по порядку: каретка всегда в конце.
            CaretIndex = Text?.Length ?? 0;
        }
    }

    protected override void OnGotFocus(FocusChangedEventArgs e)
    {
        base.OnGotFocus(e);
        _cells?.InvalidateVisual();
    }

    protected override void OnLostFocus(FocusChangedEventArgs e)
    {
        base.OnLostFocus(e);
        _cells?.InvalidateVisual();
    }

    private void OnTextChangedCore()
    {
        var text = Text ?? string.Empty;
        var clean = new string(text.Where(char.IsAsciiDigit).Take(CodeLength).ToArray());
        if (clean != text)
        {
            _normalizing = true;
            Text = clean;
            _normalizing = false;
        }

        if (HasError)
            HasError = false;

        _cells?.OnTextChanged(clean);
        CaretIndex = clean.Length;

        if (clean.Length == CodeLength)
        {
            Completed?.Invoke(this, clean);
            if (CompletedCommand?.CanExecute(clean) == true)
                CompletedCommand.Execute(clean);
        }
    }
}

/// <summary>Ячейки кода и падающие в них цифры (Playfair Display).</summary>
public sealed class CodeCellsLayer : Control
{
    private readonly List<(char Digit, TimeSpan BornAt, double Tilt)> _digits = [];
    private readonly List<(char Digit, int Cell, TimeSpan DiedAt)> _dying = [];
    /// <summary>
    /// Ровные (lining) цифры: у Playfair Display по умолчанию старостильные — 2 ростом со строчную букву, 3/4/5/7/9 уходят ниже строки.
    /// С lnum все цифры высотой с прописную и стоят на одной линии.
    /// </summary>
    private static readonly FontFeatureCollection LiningFigures = [FontFeature.Parse("lnum")];

    private readonly Dictionary<char, (FormattedText Text, Rect Ink)> _cache = [];
    private readonly Random _random = new();
    private readonly FrameClock _clock;
    private readonly BrushWatcher _brushes;
    private CodeInput? _input;
    private string _text = string.Empty;
    private (IBrush? Brush, double Size, Typeface Face) _cacheKey;

    public CodeCellsLayer()
    {
        _brushes = new BrushWatcher(this);
        IsHitTestVisible = false;
        _clock = new FrameClock(this, () =>
        {
            var now = FrameClock.Now;
            return _dying.Count > 0 || _digits.Exists(d => now - d.BornAt < GlyphMotion.AppearDuration);
        });
    }

    internal void Attach(CodeInput input)
    {
        _input = input;
        _text = input.Text ?? string.Empty;
        _digits.Clear();
        _digits.AddRange(_text.Select(c => (c, TimeSpan.Zero, 0.0)));
    }

    internal void OnTextChanged(string text)
    {
        var now = FrameClock.Now;
        var (start, removed, inserted) = GlyphMotion.Diff(_text, text);

        for (var i = 0; i < removed; i++)
        {
            _dying.Add((_digits[start].Digit, start + i, now));
            _digits.RemoveAt(start);
        }

        var step = GlyphMotion.StaggerStep(inserted);
        for (var i = 0; i < inserted; i++)
            _digits.Insert(start + i, (text[start + i], now + step * i, (_random.NextDouble() * 2 - 1) * 8));

        _text = text;
        _clock.Kick();
    }

    public override void Render(DrawingContext context)
    {
        if (_input is null)
            return;

        var now = FrameClock.Now;
        var height = Bounds.Height;
        EnsureCache();

        var inset = _input.Background;
        var error = _input.HasError && _input.TryFindResource("Mg.ErrorBrush", _input.ActualThemeVariant, out var e) ? e as IBrush : null;
        var accent = _input.BorderBrush;
        var activeCell = _input.IsFocused ? Math.Min(_digits.Count, _input.CodeLength - 1) : -1;

        for (var cell = 0; cell < _input.CodeLength; cell++)
        {
            var rect = CellRect(cell, height);
            var border = error ?? (cell == activeCell ? accent : null);
            var pen = border is null ? null : new Pen(border, 1.5);
            context.DrawRectangle(inset, pen, new RoundedRect(rect.Deflate(0.75), 14));
        }

        for (var i = 0; i < _digits.Count; i++)
        {
            var (digit, bornAt, tilt) = _digits[i];
            if (now < bornAt)
                continue;

            var (offsetY, tiltNow, opacity) = GlyphMotion.Appear(Ease.Progress(now, bornAt, GlyphMotion.AppearDuration), tilt);
            DrawDigit(context, digit, CellRect(i, height), offsetY * _cacheKey.Size, tiltNow, opacity);
        }

        for (var i = _dying.Count - 1; i >= 0; i--)
        {
            var (digit, cell, diedAt) = _dying[i];
            var t = Ease.Progress(now, diedAt, GlyphMotion.DisappearDuration);
            if (t >= 1)
            {
                _dying.RemoveAt(i);
                continue;
            }

            var (offsetY, opacity) = GlyphMotion.Disappear(t);
            DrawDigit(context, digit, CellRect(cell, height), offsetY * _cacheKey.Size, 0, opacity);
        }
    }

    private static Rect CellRect(int cell, double height) =>
        new(cell * (CodeInput.CellWidth + CodeInput.CellGap), 0, CodeInput.CellWidth, height);

    private void DrawDigit(DrawingContext context, char digit, Rect cell, double offsetY, double tiltDegrees, double opacity)
    {
        if (opacity <= 0.001)
            return;

        // По центру ячейки — центр контура самой цифры, а не строки текста.
        var (text, ink) = GetGlyph(digit);
        var topLeft = new Point(cell.Center.X - ink.Center.X, cell.Center.Y - ink.Center.Y);
        var matrix = Matrix.CreateTranslation(-cell.Center.X, -cell.Center.Y)
                     * Matrix.CreateRotation(tiltDegrees * Math.PI / 180)
                     * Matrix.CreateTranslation(cell.Center.X, cell.Center.Y + offsetY);

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
        _brushes.Watch(_input!.Foreground, _input.Background, _input.BorderBrush);
        var key = (_input.Foreground, _input.FontSize, new Typeface(_input.FontFamily, _input.FontStyle, _input.FontWeight));
        if (key == _cacheKey)
            return;

        _cache.Clear();
        _cacheKey = key;
    }

    /// <summary>
    /// Текст цифры и рамка её контура (относительно точки вывода текста). У Playfair Display над цифрами большой запас строки —
    /// центрирование по высоте строки сажало цифры ниже центра ячейки, поэтому по центру ставится контур.
    /// </summary>
    private (FormattedText Text, Rect Ink) GetGlyph(char digit)
    {
        if (!_cache.TryGetValue(digit, out var glyph))
        {
            var text = new FormattedText(digit.ToString(), System.Globalization.CultureInfo.InvariantCulture, FlowDirection.LeftToRight,
                _cacheKey.Face, _cacheKey.Size, _cacheKey.Brush ?? Brushes.White);
            text.SetFontFeatures(LiningFigures);
            var ink = text.BuildGeometry(default)?.Bounds ?? default;
            if (ink.Width <= 0 || ink.Height <= 0)
                ink = new Rect(0, 0, text.Width, text.Height);

            glyph = (text, ink);
            _cache[digit] = glyph;
        }

        return glyph;
    }
}
