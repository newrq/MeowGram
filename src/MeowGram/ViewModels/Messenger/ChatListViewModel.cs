using System.Collections.ObjectModel;
using Avalonia.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using MeowGram.Core.Messaging;

namespace MeowGram.ViewModels.Messenger;

/// <summary>Папка рейла: подпись, иконка, счётчик непрочитанных чатов со звуком.</summary>
public sealed partial class FolderViewModel(ChatFolder folder, string titleKey, string icon) : ObservableObject
{
    public ChatFolder Folder { get; } = folder;

    public string Title => Localization.Loc.Instance[titleKey];

    /// <summary>Имя SVG в Assets/icons.</summary>
    public string Icon { get; } = $"avares://MeowGram/Assets/icons/{icon}.svg";

    [ObservableProperty]
    public partial int UnreadCount { get; set; }

    [ObservableProperty]
    public partial bool IsSelected { get; set; }

    public bool HasUnread => UnreadCount > 0;

    partial void OnUnreadCountChanged(int value) => OnPropertyChanged(nameof(HasUnread));
}

/// <summary>
/// Список чатов выбранной папки в порядке TDLib. Пересобирается по событиям хранилища, но не чаще раза за проход
/// диспетчера (при старте TDLib присылает сотни обновлений подряд).
/// </summary>
public sealed partial class ChatListViewModel : ObservableObject, IDisposable
{
    private const int PageSize = 50;

    private readonly MessagingContext _context;
    private readonly ChatRows _rows;
    private bool _rebuildPosted;
    private bool _loading;

    public ChatListViewModel(MessagingContext context, ChatRows rows)
    {
        _context = context;
        _rows = rows;
        Folders =
        [
            new FolderViewModel(ChatFolder.All, "Folder_All", "rail-all"),
            new FolderViewModel(ChatFolder.Personal, "Folder_Personal", "rail-personal"),
            new FolderViewModel(ChatFolder.Groups, "Folder_Groups", "rail-groups"),
            new FolderViewModel(ChatFolder.Channels, "Folder_Channels", "rail-channels"),
            new FolderViewModel(ChatFolder.Bots, "Folder_Bots", "rail-bots"),
            new FolderViewModel(ChatFolder.Archive, "Folder_Archive", "rail-archive"),
        ];
        Folders[0].IsSelected = true;

        _context.Store.ListChanged += OnListChanged;
        _context.Store.ChatChanged += OnChatChanged;
        Rebuild();
    }

    public IReadOnlyList<FolderViewModel> Folders { get; }

    public ObservableCollection<ChatRowViewModel> Rows { get; } = [];

    [ObservableProperty]
    public partial ChatFolder Folder { get; private set; } = ChatFolder.All;

    public string Title => Folders.First(f => f.Folder == Folder).Title;

    [ObservableProperty]
    public partial bool IsEmpty { get; private set; }

    public void SelectFolder(ChatFolder folder)
    {
        if (Folder == folder)
            return;

        Folder = folder;
        foreach (var f in Folders)
            f.IsSelected = f.Folder == folder;
        _rows.PinList = ChatFolders.ListOf(folder);
        foreach (var row in Rows)
            row.Refresh(_rows.PinList);
        OnPropertyChanged(nameof(Title));
        Rebuild();
        _ = EnsureLoadedAsync();
    }

    /// <summary>Догрузить чаты, пока их меньше страницы (фильтр по типу может отсеять почти всё).</summary>
    public async Task EnsureLoadedAsync()
    {
        var list = ChatFolders.ListOf(Folder);
        for (var i = 0; i < 6 && Rows.Count < PageSize && !_context.ChatLists.IsComplete(list); i++)
        {
            if (!await LoadMoreAsync())
                break;
        }
    }

    /// <summary>Прокрутили к концу — догрузить ещё.</summary>
    public async Task<bool> LoadMoreAsync()
    {
        if (_loading)
            return false;

        _loading = true;
        try
        {
            return await _context.ChatLists.LoadMoreAsync(ChatFolders.ListOf(Folder), PageSize);
        }
        catch (Exception ex) when (ex is TdLib.TdException or InvalidOperationException)
        {
            return false;
        }
        finally
        {
            _loading = false;
        }
    }

    private void OnListChanged(ChatListKind _) => ScheduleRebuild();

    private void OnChatChanged(long _) => ScheduleRebuild();

    private void ScheduleRebuild()
    {
        if (_rebuildPosted)
            return;

        _rebuildPosted = true;
        Dispatcher.UIThread.Post(() =>
        {
            _rebuildPosted = false;
            Rebuild();
        }, DispatcherPriority.Background);
    }

    private void Rebuild()
    {
        var store = _context.Store;
        var list = ChatFolders.ListOf(Folder);
        var target = store.GetOrderedChats(list)
            .Where(id => ChatFolders.Matches(Folder, store.GetCategory(id)))
            .ToList();

        Sync(target);
        IsEmpty = Rows.Count == 0 && _context.ChatLists.IsComplete(list);
        UpdateFolderCounts();
    }

    /// <summary>Привести коллекцию к нужному порядку перемещениями — строки не пересоздаются, выделение не прыгает.</summary>
    private void Sync(List<long> target)
    {
        for (var i = 0; i < target.Count; i++)
        {
            if (i < Rows.Count && Rows[i].ChatId == target[i])
                continue;

            var existing = -1;
            for (var j = i + 1; j < Rows.Count; j++)
            {
                if (Rows[j].ChatId == target[i])
                {
                    existing = j;
                    break;
                }
            }

            if (existing >= 0)
                Rows.Move(existing, i);
            else
                Rows.Insert(i, _rows.Get(target[i]));
        }

        while (Rows.Count > target.Count)
            Rows.RemoveAt(Rows.Count - 1);
    }

    private void UpdateFolderCounts()
    {
        var store = _context.Store;
        foreach (var folder in Folders)
        {
            var list = ChatFolders.ListOf(folder.Folder);
            folder.UnreadCount = store.GetOrderedChats(list).Count(id =>
                ChatFolders.Matches(folder.Folder, store.GetCategory(id))
                && store.GetChat(id) is { } chat
                && (chat.UnreadCount > 0 || chat.IsMarkedAsUnread)
                && !store.IsMuted(id));
        }
    }

    public void Dispose()
    {
        _context.Store.ListChanged -= OnListChanged;
        _context.Store.ChatChanged -= OnChatChanged;
    }
}
