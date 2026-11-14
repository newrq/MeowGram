using System.Collections.Concurrent;
using MeowGram.Core.Accounts;
using MeowGram.Core.Telegram;
using TdLib;

namespace MeowGram.Core.Messaging;

/// <summary>
/// Всё для работы с чатами одного аккаунта. Подписан на обновления сессии с момента её создания,
/// поэтому <see cref="Store"/> видит каждый UpdateNewChat. Обновления переносятся в <see cref="IMainThread"/>.
/// </summary>
public sealed class MessagingContext : IDisposable
{
    private readonly TelegramSession? _session;
    private readonly IMainThread _mainThread;

    public MessagingContext(string accountId, ITdExecutor td, IMainThread mainThread, TelegramSession? session = null)
    {
        AccountId = accountId;
        Td = td;
        _mainThread = mainThread;
        _session = session;

        Store = new ChatStore();
        Messages = new MessageService(td);
        Files = new FileService(td);
        Search = new SearchService(td);
        Info = new ChatInfoService(td, Store);
        ChatLists = new ChatListService(td);

        if (_session is not null)
            _session.UpdateReceived += OnUpdateReceived;
    }

    public string AccountId { get; }

    public ITdExecutor Td { get; }

    public ChatStore Store { get; }

    public MessageService Messages { get; }

    public FileService Files { get; }

    public SearchService Search { get; }

    public ChatInfoService Info { get; }

    public ChatListService ChatLists { get; }

    /// <summary>Применяет обновление ко всем частям. Вызывать в <see cref="IMainThread"/>.</summary>
    public void Apply(TdApi.Update update)
    {
        Store.Apply(update);
        Messages.Apply(update);
        Files.Apply(update);
    }

    private void OnUpdateReceived(object? sender, TdApi.Update update) => _mainThread.Post(() => Apply(update));

    public void Dispose()
    {
        if (_session is not null)
            _session.UpdateReceived -= OnUpdateReceived;
    }
}

/// <summary>
/// Контексты чатов для всех сессий <see cref="AccountManager"/>; <see cref="Current"/> — для активного аккаунта.
/// Создаётся до запуска аккаунтов (см. AccountManagerHostedService), чтобы не пропустить первые обновления.
/// </summary>
public sealed class MessagingHub : IDisposable
{
    private readonly AccountManager _accounts;
    private readonly IMainThread _mainThread;
    private readonly ConcurrentDictionary<string, MessagingContext> _contexts = new();

    public MessagingHub(AccountManager accounts, IMainThread mainThread)
    {
        _accounts = accounts;
        _mainThread = mainThread;
        _accounts.SessionCreated += OnSessionCreated;
        _accounts.SessionRemoved += OnSessionRemoved;
        _accounts.ActiveSessionChanged += OnActiveSessionChanged;

        foreach (var session in _accounts.Sessions)
            OnSessionCreated(this, session);
    }

    /// <summary>Контекст активного аккаунта; null — нет вошедшего аккаунта.</summary>
    public MessagingContext? Current =>
        _accounts.ActiveSession is { } session ? _contexts.GetValueOrDefault(session.AccountId) : null;

    /// <summary>Сменился активный аккаунт. Приходит в <see cref="IMainThread"/>.</summary>
    public event EventHandler? CurrentChanged;

    public MessagingContext? Get(string accountId) => _contexts.GetValueOrDefault(accountId);

    private void OnSessionCreated(object? sender, TelegramSession session) =>
        _contexts.GetOrAdd(session.AccountId, _ => new MessagingContext(session.AccountId, session, _mainThread, session));

    private void OnSessionRemoved(object? sender, TelegramSession session)
    {
        if (_contexts.TryRemove(session.AccountId, out var context))
            context.Dispose();
    }

    private void OnActiveSessionChanged(object? sender, EventArgs e) =>
        _mainThread.Post(() => CurrentChanged?.Invoke(this, EventArgs.Empty));

    public void Dispose()
    {
        _accounts.SessionCreated -= OnSessionCreated;
        _accounts.SessionRemoved -= OnSessionRemoved;
        _accounts.ActiveSessionChanged -= OnActiveSessionChanged;
        foreach (var context in _contexts.Values)
            context.Dispose();
        _contexts.Clear();
    }
}
