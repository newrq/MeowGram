using Avalonia.Controls;
using MeowGram.ViewModels.Messenger;

namespace MeowGram.Views.Messenger;

public partial class ChatListPanel : UserControl
{
    public ChatListPanel()
    {
        InitializeComponent();
        // Докрутили до конца — догрузить ещё чатов.
        Scroller.ScrollChanged += async (_, _) =>
        {
            if (DataContext is MessengerViewModel { ChatList: { } list }
                && Scroller.Offset.Y + Scroller.Viewport.Height > Scroller.Extent.Height - 400)
                await list.LoadMoreAsync();
        };
    }
}
