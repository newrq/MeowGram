using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using MeowGram.Core.Messaging;
using MeowGram.ViewModels.Messenger.Feed;

namespace MeowGram.ViewModels.Messenger;

/// <summary>Выбор чата для пересылки: список основных чатов с фильтром по названию.</summary>
public sealed partial class ForwardPickerViewModel : ObservableObject
{
    private readonly MessagingContext _context;
    private readonly ChatRows _rows;
    private readonly MessageViewModel _message;
    private readonly Action _close;

    public ForwardPickerViewModel(MessagingContext context, ChatRows rows, MessageViewModel message, Action close)
    {
        _context = context;
        _rows = rows;
        _message = message;
        _close = close;
        Filter();
    }

    [ObservableProperty]
    public partial string Query { get; set; } = string.Empty;

    public ObservableCollection<ChatRowViewModel> Chats { get; } = [];

    partial void OnQueryChanged(string value) => Filter();

    [RelayCommand]
    private async Task PickAsync(ChatRowViewModel? row)
    {
        if (row is null)
            return;

        _close();
        try
        {
            await _context.Messages.ForwardAsync(row.ChatId, _message.ChatId, [_message.Id]);
        }
        catch (Exception ex) when (ex is TdLib.TdException or InvalidOperationException)
        {
            System.Diagnostics.Debug.WriteLine($"Forward failed: {ex.Message}");
        }
    }

    [RelayCommand]
    private void Cancel() => _close();

    private void Filter()
    {
        var query = Query.Trim();
        Chats.Clear();
        foreach (var id in _context.Store.GetOrderedChats(ChatListKind.Main))
        {
            var row = _rows.Get(id);
            if (row.IsChannel)
                continue;
            if (query.Length == 0 || row.Title.Contains(query, StringComparison.CurrentCultureIgnoreCase))
                Chats.Add(row);
            if (Chats.Count >= 100)
                break;
        }
    }
}
