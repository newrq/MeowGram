using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using MeowGram.Core.Messaging;
using MeowGram.Core.Settings;
using MeowGram.Localization;

namespace MeowGram.ViewModels.Messenger;

/// <summary>Куда сейчас наведён перетаскиваемый чат.</summary>
public abstract record DropTarget
{
    public sealed record Pane(int Index) : DropTarget;

    public sealed record Ghost : DropTarget;
}

/// <summary>
/// Разделённые чаты: до 4 окон (<see cref="SplitLayout"/>), активное окно, состояние перетаскивания (пустой слот,
/// подписи над окнами) и сохранение открытых окон для аккаунта.
/// </summary>
public sealed partial class SplitViewModel : ObservableObject
{
    private readonly MessagingContext _context;
    private readonly ChatRows _rows;
    private readonly ISettingsStore _settings;
    private readonly Func<long, ChatPaneViewModel> _createPane;

    public SplitViewModel(MessagingContext context, ChatRows rows, ISettingsStore settings, Func<long, ChatPaneViewModel> createPane)
    {
        _context = context;
        _rows = rows;
        _settings = settings;
        _createPane = createPane;
    }

    public SplitLayout Layout { get; private set; } = SplitLayout.Empty;

    /// <summary>Окна по порядку (окно 1 — первое).</summary>
    public ObservableCollection<ChatPaneViewModel> Panes { get; } = [];

    /// <summary>Ячейки сетки: окна и, при перетаскивании, пустой слот последним.</summary>
    public ObservableCollection<object> Cells { get; } = [];

    /// <summary>Пустой слот (одна модель на всё время).</summary>
    public SplitGhostViewModel Ghost => _ghost ??= new SplitGhostViewModel(this);

    private SplitGhostViewModel? _ghost;

    public ChatPaneViewModel? ActivePane => Layout.Active is { } id ? Panes.FirstOrDefault(p => p.ChatId == id) : null;

    public bool HasPanes => Panes.Count > 0;

    public bool IsEmpty => Panes.Count == 0 && !ShowGhost;

    // ── Перетаскивание ────────────────────────────────────────────────

    /// <summary>Какой чат тащат (0 — ничего).</summary>
    [ObservableProperty]
    public partial long DraggedChatId { get; private set; }

    [ObservableProperty]
    public partial DropTarget? Hover { get; private set; }

    public bool IsDragging => DraggedChatId != 0;

    /// <summary>Пустой слот «Отпустите, чтобы открыть рядом»: тащат неоткрытый чат и есть место.</summary>
    public bool ShowGhost => IsDragging && Layout.CanAddBeside(DraggedChatId);

    public bool IsGhostHot => Hover is DropTarget.Ghost;

    public string GhostCaption => Loc.Instance.Format("Split_GhostSlot", Layout.Count + 1, SplitLayout.MaxPanes);

    /// <summary>Сколько ячеек в сетке сейчас (окна + пустой слот).</summary>
    public int CellCount => Panes.Count + (ShowGhost ? 1 : 0);

    public string OpenLabel => IsDragging
        ? Loc.Instance["Split_DragHint"]
        : Layout.Count == 0 ? Loc.Instance["Split_NoWindows"] : Loc.Instance.Format("Split_WindowsOf", Layout.Count, SplitLayout.MaxPanes);

    /// <summary>Восстановить окна, открытые в прошлый раз.</summary>
    public async Task RestoreAsync()
    {
        try
        {
            var saved = await _settings.GetAsync<SavedSplit>(KeyOf(_context.AccountId));
            if (saved is null)
                return;

            var existing = saved.Open.Where(id => _context.Store.GetChat(id) is not null).ToArray();
            await ApplyAsync(new SplitLayout(existing, saved.Active), save: false);
        }
        catch (System.Text.Json.JsonException)
        {
            // Старый формат — начинаем с пустого сплита.
        }
    }

    /// <summary>Клик в списке — открыть в активном окне.</summary>
    public Task OpenAsync(long chatId, long focusMessageId = 0)
    {
        if (focusMessageId != 0 && Panes.FirstOrDefault(p => p.ChatId == chatId) is { } pane)
        {
            _ = ApplyAsync(Layout.Focus(chatId));
            return pane.JumpToAsync(focusMessageId);
        }

        return ApplyAsync(Layout.OpenInActive(chatId), focusMessageId);
    }

    public Task CloseAsync(long chatId) => ApplyAsync(Layout.Close(chatId));

    public Task FocusAsync(long chatId) => ApplyAsync(Layout.Focus(chatId));

    public Task FocusSlotAsync(int index) => ApplyAsync(Layout.FocusSlot(index));

    public void BeginDrag(long chatId)
    {
        DraggedChatId = chatId;
        Hover = null;
        foreach (var row in _rows.All)
            row.IsDragged = row.ChatId == chatId;
        RaiseDrag();
    }

    public void SetHover(DropTarget? target)
    {
        if (Hover == target)
            return;
        Hover = target;
        RaiseDrag();
    }

    /// <summary>Подпись на окне под курсором: заменить, поменять местами или «уже здесь».</summary>
    public string DropLabel(int paneIndex) => Layout.DescribeDrop(paneIndex, DraggedChatId) switch
    {
        PaneDropAction.AlreadyHere => Loc.Instance["Split_AlreadyHere"],
        PaneDropAction.Swap => Loc.Instance["Split_Swap"],
        _ => Loc.Instance.Format("Split_ReplaceWith", Panes.ElementAtOrDefault(paneIndex)?.Row.Title ?? string.Empty),
    };

    /// <summary>Отпустили: применить бросок. Возвращает, принят ли он (иначе карточка летит обратно).</summary>
    public async Task<bool> DropAsync()
    {
        var chatId = DraggedChatId;
        var target = Hover;
        EndDrag();

        var next = target switch
        {
            DropTarget.Pane pane => Layout.DropOnPane(pane.Index, chatId),
            DropTarget.Ghost => Layout.DropOnGhost(chatId),
            _ => Layout,
        };

        if (ReferenceEquals(next, Layout) || next.Equals(Layout) && target is not DropTarget.Pane)
            return false;

        await ApplyAsync(next);
        return true;
    }

    public void EndDrag()
    {
        DraggedChatId = 0;
        Hover = null;
        foreach (var row in _rows.All)
            row.IsDragged = false;
        RaiseDrag();
    }

    /// <summary>Привести окна к раскладу: открыть новые, закрыть лишние, переставить, обновить активное.</summary>
    private async Task ApplyAsync(SplitLayout next, long focusMessageId = 0, bool save = true)
    {
        var previous = Layout;
        Layout = next;

        var removed = Panes.Where(p => !next.Contains(p.ChatId)).ToArray();
        foreach (var pane in removed)
        {
            Panes.Remove(pane);
            _ = pane.DisposeAsync().AsTask();
        }

        for (var i = 0; i < next.Open.Count; i++)
        {
            var chatId = next.Open[i];
            var existing = Panes.FirstOrDefault(p => p.ChatId == chatId);
            if (existing is null)
            {
                var pane = _createPane(chatId);
                Panes.Insert(Math.Min(i, Panes.Count), pane);
                _ = pane.OpenAsync(chatId == next.Active ? focusMessageId : 0);
            }
            else
            {
                var at = Panes.IndexOf(existing);
                if (at != i)
                    Panes.Move(at, i);
            }
        }

        for (var i = 0; i < Panes.Count; i++)
        {
            Panes[i].IsActive = Panes[i].ChatId == next.Active;
            Panes[i].IsCompact = Panes.Count > 1;
            Panes[i].Slot = i + 1;
        }

        foreach (var row in _rows.All)
        {
            var at = next.IndexOf(row.ChatId);
            row.OpenSlot = at + 1;
            row.IsActiveSlot = row.ChatId == next.Active;
        }

        OnPropertyChanged(nameof(ActivePane));
        OnPropertyChanged(nameof(HasPanes));
        RaiseDrag();

        if (save && !previous.Equals(next))
        {
            try
            {
                await _settings.SetAsync(KeyOf(_context.AccountId), new SavedSplit([.. next.Open], next.Active));
            }
            catch (Exception ex) when (ex is IOException or InvalidOperationException)
            {
                // Не сохранилось — в следующий раз откроется пустой сплит.
            }
        }
    }

    /// <summary>Строки, созданные после применения расклада (догрузка списка), тоже получают номер окна.</summary>
    public void SyncRow(ChatRowViewModel row)
    {
        row.OpenSlot = Layout.IndexOf(row.ChatId) + 1;
        row.IsActiveSlot = row.ChatId == Layout.Active;
    }

    private void RaiseDrag()
    {
        SyncCells();
        OnPropertyChanged(nameof(IsDragging));
        OnPropertyChanged(nameof(ShowGhost));
        OnPropertyChanged(nameof(IsGhostHot));
        OnPropertyChanged(nameof(GhostCaption));
        OnPropertyChanged(nameof(CellCount));
        OnPropertyChanged(nameof(OpenLabel));
        OnPropertyChanged(nameof(IsEmpty));
    }

    private void SyncCells()
    {
        var target = new List<object>(Panes);
        if (ShowGhost)
            target.Add(Ghost);

        for (var i = 0; i < target.Count; i++)
        {
            if (i < Cells.Count && ReferenceEquals(Cells[i], target[i]))
                continue;
            var at = -1;
            for (var j = i + 1; j < Cells.Count; j++)
            {
                if (ReferenceEquals(Cells[j], target[i]))
                {
                    at = j;
                    break;
                }
            }

            if (at >= 0)
                Cells.Move(at, i);
            else
                Cells.Insert(i, target[i]);
        }

        while (Cells.Count > target.Count)
            Cells.RemoveAt(Cells.Count - 1);
    }

    private static string KeyOf(string accountId) => $"ui.split.{accountId}";

    private sealed record SavedSplit(long[] Open, long? Active);
}

/// <summary>Пустой слот «Отпустите, чтобы открыть рядом»; подписи берёт у сплита.</summary>
public sealed class SplitGhostViewModel(SplitViewModel owner)
{
    public SplitViewModel Owner { get; } = owner;
}
