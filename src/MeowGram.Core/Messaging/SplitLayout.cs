namespace MeowGram.Core.Messaging;

/// <summary>Что произойдёт, если отпустить чат над окном.</summary>
public enum PaneDropAction
{
    /// <summary>Открыть перетаскиваемый чат вместо чата окна.</summary>
    Replace,

    /// <summary>Перетаскиваемый чат уже открыт в другом окне — окна поменяются местами.</summary>
    Swap,

    /// <summary>Этот чат и так в этом окне.</summary>
    AlreadyHere,
}

/// <summary>Ячейка сетки окон: столбец, строка и сколько строк занимает окно.</summary>
public readonly record struct PaneCell(int Column, int Row, int RowSpan = 1);

/// <summary>Сетка окон: размер и ячейка каждого окна по порядку.</summary>
public sealed record PaneGrid(int Columns, int Rows, IReadOnlyList<PaneCell> Cells);

/// <summary>
/// Разделённые чаты: до <see cref="MaxPanes"/> окон рядом и активное окно. Неизменяемый — каждое действие
/// возвращает новый расклад (удобно сравнивать и сохранять).
/// </summary>
public sealed class SplitLayout : IEquatable<SplitLayout>
{
    public const int MaxPanes = 4;

    public static SplitLayout Empty { get; } = new([], null);

    private readonly long[] _open;

    public SplitLayout(IEnumerable<long> open, long? active)
    {
        _open = open.Distinct().Take(MaxPanes).ToArray();
        Active = active is { } a && _open.Contains(a) ? a : _open.Length > 0 ? _open[0] : null;
    }

    /// <summary>Открытые чаты по порядку окон (окно 1, 2, …).</summary>
    public IReadOnlyList<long> Open => _open;

    /// <summary>Чат активного окна — туда открывается чат по клику в списке.</summary>
    public long? Active { get; }

    public int Count => _open.Length;

    public int ActiveIndex => Active is { } a ? Array.IndexOf(_open, a) : -1;

    public int IndexOf(long chatId) => Array.IndexOf(_open, chatId);

    public bool Contains(long chatId) => IndexOf(chatId) >= 0;

    /// <summary>Клик в списке: уже открытый чат становится активным, иначе заменяет чат активного окна.</summary>
    public SplitLayout OpenInActive(long chatId)
    {
        if (Contains(chatId))
            return Focus(chatId);
        if (_open.Length == 0)
            return new SplitLayout([chatId], chatId);

        var next = _open.ToArray();
        next[Math.Max(ActiveIndex, 0)] = chatId;
        return new SplitLayout(next, chatId);
    }

    public SplitLayout Focus(long chatId) => Contains(chatId) ? new SplitLayout(_open, chatId) : this;

    /// <summary>Alt+1–4: сделать активным окно с номером <paramref name="index"/> (с нуля).</summary>
    public SplitLayout FocusSlot(int index) => index >= 0 && index < _open.Length ? Focus(_open[index]) : this;

    /// <summary>Закрыть окно с чатом. Если оно было активным, активным станет первое оставшееся.</summary>
    public SplitLayout Close(long chatId)
    {
        if (!Contains(chatId))
            return this;

        var next = _open.Where(id => id != chatId).ToArray();
        return new SplitLayout(next, Active == chatId ? next.FirstOrDefault() : Active);
    }

    public PaneDropAction DescribeDrop(int paneIndex, long chatId)
    {
        var at = IndexOf(chatId);
        return at == paneIndex ? PaneDropAction.AlreadyHere : at >= 0 ? PaneDropAction.Swap : PaneDropAction.Replace;
    }

    /// <summary>Отпустили чат над окном: заменить его чат или поменять окна местами. Чат становится активным.</summary>
    public SplitLayout DropOnPane(int paneIndex, long chatId)
    {
        if (paneIndex < 0 || paneIndex >= _open.Length)
            return this;

        var next = _open.ToArray();
        var at = Array.IndexOf(next, chatId);
        if (at >= 0)
            (next[at], next[paneIndex]) = (next[paneIndex], next[at]);
        else
            next[paneIndex] = chatId;

        return new SplitLayout(next, chatId);
    }

    /// <summary>Можно ли открыть чат рядом (в новом окне): есть место и чат ещё не открыт.</summary>
    public bool CanAddBeside(long chatId) => _open.Length < MaxPanes && !Contains(chatId);

    /// <summary>Отпустили чат в пустой слот: открыть рядом и сделать активным.</summary>
    public SplitLayout DropOnGhost(long chatId) =>
        CanAddBeside(chatId) ? new SplitLayout([.. _open, chatId], chatId) : this;

    /// <summary>
    /// Раскладка для <paramref name="count"/> окон (с учётом пустого слота при перетаскивании):
    /// 1 — на всю ширину, 2 — две колонки, 3 — первое окно на две строки слева, 4 — сетка 2×2.
    /// </summary>
    public static PaneGrid Arrange(int count) => count switch
    {
        <= 0 => new PaneGrid(1, 1, []),
        1 => new PaneGrid(1, 1, [new PaneCell(0, 0)]),
        2 => new PaneGrid(2, 1, [new PaneCell(0, 0), new PaneCell(1, 0)]),
        3 => new PaneGrid(2, 2, [new PaneCell(0, 0, RowSpan: 2), new PaneCell(1, 0), new PaneCell(1, 1)]),
        _ => new PaneGrid(2, 2, [new PaneCell(0, 0), new PaneCell(1, 0), new PaneCell(0, 1), new PaneCell(1, 1)]),
    };

    public bool Equals(SplitLayout? other) =>
        other is not null && Active == other.Active && _open.AsSpan().SequenceEqual(other._open);

    public override bool Equals(object? obj) => Equals(obj as SplitLayout);

    public override int GetHashCode()
    {
        var hash = new HashCode();
        hash.Add(Active);
        foreach (var id in _open)
            hash.Add(id);
        return hash.ToHashCode();
    }
}
