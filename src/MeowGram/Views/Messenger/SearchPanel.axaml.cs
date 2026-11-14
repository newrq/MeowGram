using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.LogicalTree;
using Avalonia.Threading;
using MeowGram.ViewModels.Messenger;

namespace MeowGram.Views.Messenger;

public partial class SearchPanel : UserControl
{
    public SearchPanel()
    {
        InitializeComponent();
        QueryBox.KeyDown += OnQueryKeyDown;
        Scroller.ScrollChanged += async (_, _) =>
        {
            if (DataContext is SearchViewModel search && Scroller.Offset.Y + Scroller.Viewport.Height > Scroller.Extent.Height - 300)
                await search.LoadMoreMessagesAsync();
        };
        DataContextChanged += (_, _) => UpdateTabs();
        PropertyChanged += (_, e) =>
        {
            if (e.Property == IsVisibleProperty && IsVisible)
                FocusQuery();
        };
    }

    /// <summary>«Назад» в шапке поиска — закрывает поиск (обработчик ставит MessengerView).</summary>
    public Button Back => BackButton;

    public void FocusQuery() => Dispatcher.UIThread.Post(() => QueryBox.Focus(), DispatcherPriority.Input);

    private void OnQueryKeyDown(object? sender, KeyEventArgs e)
    {
        if (e.Key == Key.Enter && DataContext is SearchViewModel search && search.OpenFirst())
            e.Handled = true;
    }

    protected override void OnDataContextChanged(EventArgs e)
    {
        base.OnDataContextChanged(e);
        if (DataContext is SearchViewModel search)
            search.PropertyChanged += (_, args) =>
            {
                if (args.PropertyName == nameof(SearchViewModel.Tab))
                    UpdateTabs();
            };
    }

    /// <summary>Подсветка выбранной вкладки (класс selected).</summary>
    private void UpdateTabs()
    {
        if (DataContext is not SearchViewModel search)
            return;

        foreach (var tab in this.GetLogicalDescendants().OfType<Button>().Where(b => b.Classes.Contains("tab")))
            tab.Classes.Set("selected", tab.Tag is SearchTab t && t == search.Tab);
    }
}
