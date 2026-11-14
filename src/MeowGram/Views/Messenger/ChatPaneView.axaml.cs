using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Input;
using Avalonia.Input.Platform;
using Avalonia.Interactivity;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Platform.Storage;
using Avalonia.Styling;
using Avalonia.Threading;
using Avalonia.VisualTree;
using MeowGram.Controls;
using MeowGram.Localization;
using MeowGram.ViewModels.Messenger;
using MeowGram.ViewModels.Messenger.Feed;

namespace MeowGram.Views.Messenger;

public partial class ChatPaneView : UserControl
{
    private static readonly string[] Emoji =
    [
        "😀", "😂", "🥲", "😊", "😍", "😘", "😎", "🤔", "😴", "😭", "😡", "🤯",
        "👍", "👎", "👏", "🙏", "💪", "👀", "🔥", "✨", "🎉", "❤️", "💔", "💯",
        "😺", "😸", "😹", "😻", "😼", "🙀", "😿", "😾", "🐈", "🐾", "🐟", "🥛",
    ];

    private ChatPaneViewModel? _pane;
    private bool _loadingOlder;
    private DispatcherTimer? _visibleTimer;

    public ChatPaneView()
    {
        InitializeComponent();

        ComposerBox.AddHandler(KeyDownEvent, OnComposerKeyDown, RoutingStrategies.Tunnel);
        Feed.ScrollChanged += OnFeedScrolled;
        AttachButton.Click += OnAttachClick;
        EmojiButton.Click += (_, _) => ShowEmoji();
        MoreButton.Click += (_, _) => ShowMore();
        CompactHeader.Click += OnCompactHeaderClick;

        // Клики внутри пузырей: цитата, превью ссылки, реакция, повтор, волна голосового; правая кнопка — меню.
        FeedItems.AddHandler(Button.ClickEvent, OnFeedButtonClick);
        FeedItems.AddHandler(PointerPressedEvent, OnFeedPointerPressed, RoutingStrategies.Tunnel);
        FeedItems.AddHandler(ContextRequestedEvent, OnFeedContextRequested);
    }

    protected override void OnDataContextChanged(EventArgs e)
    {
        base.OnDataContextChanged(e);
        if (_pane is not null)
        {
            _pane.ScrollRequested -= OnScrollRequested;
            _pane.ComposerFocusRequested -= FocusComposer;
            _pane.CopyRequested -= Copy;
        }

        _pane = DataContext as ChatPaneViewModel;
        if (_pane is not null)
        {
            _pane.ScrollRequested += OnScrollRequested;
            _pane.ComposerFocusRequested += FocusComposer;
            _pane.CopyRequested += Copy;
        }
    }

    private void FocusComposer() => Dispatcher.UIThread.Post(() =>
    {
        ComposerBox.Focus();
        ComposerBox.CaretIndex = ComposerBox.Text?.Length ?? 0;
    }, DispatcherPriority.Input);

    private async void Copy(string text)
    {
        if (TopLevel.GetTopLevel(this)?.Clipboard is { } clipboard)
            await clipboard.SetTextAsync(text);
    }

    // ── Поле ввода ────────────────────────────────────────────────────

    private void OnComposerKeyDown(object? sender, KeyEventArgs e)
    {
        if (_pane is null)
            return;

        switch (e.Key)
        {
            case Key.Enter when !e.KeyModifiers.HasFlag(KeyModifiers.Shift):
                _pane.SendCommand.Execute(null);
                e.Handled = true;
                break;
            case Key.Up when string.IsNullOrEmpty(ComposerBox.Text) && _pane.EditLastOutgoing():
                e.Handled = true;
                break;
        }
    }

    private async void OnAttachClick(object? sender, RoutedEventArgs e)
    {
        if (_pane is null || TopLevel.GetTopLevel(this)?.StorageProvider is not { } storage)
            return;

        var files = await storage.OpenFilePickerAsync(new FilePickerOpenOptions
        {
            AllowMultiple = true,
            Title = Loc.Instance["Tip_Attach"],
        });

        var paths = files.Select(f => f.TryGetLocalPath()).OfType<string>().ToArray();
        if (paths.Length > 0)
            await _pane.SendFilesAsync(paths);
    }

    private void ShowEmoji()
    {
        var panel = new WrapPanel { Width = 300 };
        var flyout = new Flyout { Placement = PlacementMode.TopEdgeAlignedRight, Content = MenuCard(panel) };
        ApplyMenuTheme(flyout);
        foreach (var emoji in Emoji)
        {
            var button = new Button
            {
                Theme = (ControlTheme)this.FindResource("Mg.MenuItemButton")!,
                Width = 44,
                Height = 40,
                MinHeight = 40,
                Padding = new Thickness(0),
                HorizontalContentAlignment = HorizontalAlignment.Center,
                Content = new TextBlock { Text = emoji, FontSize = 20, HorizontalAlignment = HorizontalAlignment.Center },
            };
            button.Click += (_, _) =>
            {
                if (_pane is null)
                    return;
                var caret = _pane.InsertText(emoji, ComposerBox.CaretIndex);
                ComposerBox.CaretIndex = caret;
                ComposerBox.Focus();
            };
            panel.Children.Add(button);
        }

        flyout.ShowAt(EmojiButton);
    }

    private void ShowMore()
    {
        if (_pane is null)
            return;

        var pane = _pane;
        ShowMenu(MoreButton, false,
            Item("chat-search.svg", "Tip_SearchInChat", () => pane.ToggleSearchCommand.Execute(null)),
            Item("chat-panel.svg", "Tip_Profile", () => pane.ToggleInfoCommand.Execute(null)),
            Separator(),
            Item("close.svg", "Tip_CloseWindow", () => pane.ClosePaneCommand.Execute(null)));
    }

    private void OnCompactHeaderClick(object? sender, RoutedEventArgs e)
    {
        if (_pane is null || DragGuard.ConsumeClick())
            return;

        // Неактивное окно — сделать активным; активное — открыть или закрыть инфо-панель.
        if (_pane.IsActive)
            _pane.ToggleInfoCommand.Execute(null);
        else
            _pane.FocusPaneCommand.Execute(null);
    }

    // ── Лента ─────────────────────────────────────────────────────────

    private void OnFeedButtonClick(object? sender, RoutedEventArgs e)
    {
        if (_pane is null || e.Source is not Button button)
            return;

        var message = FindMessage(button);
        if (button.Classes.Contains("replyQuote") && message is not null)
            _pane.OpenReplyCommand.Execute(message);
        else if (button.Classes.Contains("linkPreview") && button.Tag is string url)
            RichMessageText.Open(url);
        else if (button.Classes.Contains("retry") && message is not null)
            _pane.RetryCommand.Execute(message);
        else if (button.Classes.Contains("reaction") && button.DataContext is ReactionViewModel reaction && message is not null)
            _pane.ToggleReactionCommand.Execute(new ReactionCommandArgs(message, reaction));
        else
            return;

        e.Handled = true;
    }

    private void OnFeedPointerPressed(object? sender, PointerPressedEventArgs e)
    {
        // Клик по волне голосового — перемотка.
        if (e.Source is Visual source && source.FindAncestorOfType<ItemsControl>(includeSelf: true) is { } beans
            && beans.Classes.Contains("beans") && beans.DataContext is VoiceContentViewModel voice)
        {
            var x = e.GetPosition(beans).X;
            voice.Seek(Math.Clamp(x / Math.Max(1, beans.Bounds.Width), 0, 1));
            e.Handled = true;
        }
    }

    private void OnFeedContextRequested(object? sender, ContextRequestedEventArgs e)
    {
        if (_pane is null || e.Source is not Control source || FindMessage(source) is not { } message)
            return;

        var pane = _pane;
        var items = new List<Control>
        {
            Item("reply.svg", "Menu_Reply", () => pane.ReplyCommand.Execute(message)),
        };
        if (message.PlainText.Length > 0)
            items.Add(Item("copy.svg", "Menu_Copy", () => pane.CopyCommand.Execute(message)));
        items.Add(Item("forward.svg", "Menu_Forward", () => pane.ForwardCommand.Execute(message)));
        if (message.CanEdit)
            items.Add(Item("pencil.svg", "Menu_Edit", () => pane.EditCommand.Execute(message)));
        items.Add(Item("pin.svg", message.IsPinned ? "Menu_Unpin" : "Menu_Pin", () => pane.TogglePinCommand.Execute(message)));
        items.Add(Separator());
        items.Add(Item("trash.svg", "Menu_Delete", () => pane.DeleteCommand.Execute(message), danger: true));

        ShowMenu(source, true, [.. items]);
        e.Handled = true;
    }

    private static MessageViewModel? FindMessage(Control control)
    {
        for (Visual? v = control; v is not null; v = v.GetVisualParent())
        {
            if (v is StyledElement { DataContext: MessageViewModel message })
                return message;
            if (v is StyledElement { DataContext: MessageContentViewModel content })
                return content.Owner;
        }

        return null;
    }

    private void OnFeedScrolled(object? sender, ScrollChangedEventArgs e)
    {
        if (_pane is null)
            return;

        var bottomGap = Feed.Extent.Height - Feed.Viewport.Height - Feed.Offset.Y;
        _pane.IsAtBottom = bottomGap < 40;

        if (Feed.Offset.Y < 400 && _pane.HasOlder && !_loadingOlder && !_pane.IsLoading)
            _ = LoadOlderKeepingPositionAsync();
        if (bottomGap < 400 && _pane.HasNewer)
            _ = _pane.LoadNewerAsync();

        ScheduleMarkVisible();
    }

    /// <summary>Старые сообщения добавляются сверху — сохраняем расстояние до низа, чтобы лента не прыгала.</summary>
    private async Task LoadOlderKeepingPositionAsync()
    {
        if (_pane is null)
            return;

        _loadingOlder = true;
        var fromBottom = Feed.Extent.Height - Feed.Offset.Y;
        try
        {
            await _pane.LoadOlderAsync();
            FeedStack.UpdateLayout();
            Feed.UpdateLayout();
            Feed.Offset = new Vector(Feed.Offset.X, Math.Max(0, Feed.Extent.Height - fromBottom));
        }
        finally
        {
            _loadingOlder = false;
        }
    }

    private void OnScrollRequested(FeedItemViewModel? item) => Dispatcher.UIThread.Post(() =>
    {
        FeedStack.UpdateLayout();
        if (item is null)
        {
            Feed.Offset = new Vector(Feed.Offset.X, Math.Max(0, Feed.Extent.Height - Feed.Viewport.Height));
        }
        else if (FeedItems.ContainerFromItem(item) is { } container)
        {
            // Элемент — по центру экрана (или к верху, если он выше экрана).
            var top = container.TranslatePoint(default, FeedStack)?.Y ?? 0;
            var target = top - Math.Max(0, (Feed.Viewport.Height - container.Bounds.Height) / 2);
            Feed.Offset = new Vector(Feed.Offset.X, Math.Clamp(target, 0, Math.Max(0, Feed.Extent.Height - Feed.Viewport.Height)));
        }

        ScheduleMarkVisible();
    }, DispatcherPriority.Loaded);

    private void ScheduleMarkVisible()
    {
        _visibleTimer ??= new DispatcherTimer(TimeSpan.FromMilliseconds(350), DispatcherPriority.Background, (_, _) =>
        {
            _visibleTimer!.Stop();
            MarkVisible();
        });
        _visibleTimer.Stop();
        _visibleTimer.Start();
    }

    /// <summary>Сообщения, видимые на экране, — прочитаны (только для видимого окна приложения).</summary>
    private void MarkVisible()
    {
        if (_pane is null || TopLevel.GetTopLevel(this) is not Window { IsActive: true })
            return;

        var viewTop = Feed.Offset.Y;
        var viewBottom = viewTop + Feed.Viewport.Height;
        var visible = new List<MessageViewModel>();
        foreach (var item in _pane.Items)
        {
            if (item is not MessageViewModel message || FeedItems.ContainerFromItem(item) is not { } container)
                continue;

            var top = container.TranslatePoint(default, FeedStack)?.Y ?? -1;
            if (top + container.Bounds.Height > viewTop && top < viewBottom)
                visible.Add(message);
        }

        _pane.MarkVisible(visible);
    }

    // ── Меню ──────────────────────────────────────────────────────────

    private Control Item(string icon, string textKey, Action action, bool danger = false)
    {
        var button = new Button
        {
            Theme = (ControlTheme)this.FindResource("Mg.MenuItemButton")!,
            HorizontalContentAlignment = HorizontalAlignment.Left,
            Content = new StackPanel
            {
                Orientation = Orientation.Horizontal,
                Spacing = 12,
                Children =
                {
                    new SvgIcon { Source = $"avares://MeowGram/Assets/icons/{icon}", Width = 17, Height = 17 },
                    new TextBlock { Text = Loc.Instance[textKey], VerticalAlignment = VerticalAlignment.Center },
                },
            },
        };
        if (danger)
            button.Classes.Add("danger");
        button.Tag = action;
        return button;
    }

    private static Control Separator() => new Border
    {
        Height = 1,
        Margin = new Thickness(8, 4),
        [!Border.BackgroundProperty] = new Avalonia.Markup.Xaml.MarkupExtensions.DynamicResourceExtension("Mg.LineBrush"),
    };

    private void ShowMenu(Control target, bool atPointer, params Control[] items)
    {
        var stack = new StackPanel { MinWidth = 200 };
        foreach (var item in items)
            stack.Children.Add(item);

        var flyout = new Flyout { Content = MenuCard(stack), Placement = atPointer ? PlacementMode.Pointer : PlacementMode.BottomEdgeAlignedRight };
        ApplyMenuTheme(flyout);
        foreach (var button in items.OfType<Button>())
        {
            button.Click += (_, _) =>
            {
                flyout.Hide();
                (button.Tag as Action)?.Invoke();
            };
        }

        flyout.ShowAt(target, atPointer);
    }

    /// <summary>Карточка меню по макету: Surface, рамка Line, скругление 18, отступ 6, тень.</summary>
    private static Border MenuCard(Control content) => new()
    {
        Padding = new Thickness(6),
        CornerRadius = new CornerRadius(18),
        BorderThickness = new Thickness(1),
        BoxShadow = BoxShadows.Parse("0 16 40 0 #59000000"),
        Child = content,
        [!Border.BackgroundProperty] = new Avalonia.Markup.Xaml.MarkupExtensions.DynamicResourceExtension("Mg.SurfaceBrush"),
        [!Border.BorderBrushProperty] = new Avalonia.Markup.Xaml.MarkupExtensions.DynamicResourceExtension("Mg.LineBrush"),
    };

    private void ApplyMenuTheme(Flyout flyout)
    {
        if (this.TryFindResource("Mg.MenuFlyoutPresenter", out var theme) && theme is ControlTheme controlTheme)
            flyout.FlyoutPresenterTheme = controlTheme;
    }
}

/// <summary>
/// Перетаскивание и клик приходят от одной кнопки: после перетаскивания её Click нужно пропустить.
/// MessengerView отмечает конец перетаскивания, кнопки спрашивают перед действием.
/// </summary>
public static class DragGuard
{
    private static DateTime _dragEndedAt;

    public static void MarkDragEnded() => _dragEndedAt = DateTime.UtcNow;

    public static bool ConsumeClick()
    {
        var recent = DateTime.UtcNow - _dragEndedAt < TimeSpan.FromMilliseconds(400);
        _dragEndedAt = default;
        return recent;
    }
}
