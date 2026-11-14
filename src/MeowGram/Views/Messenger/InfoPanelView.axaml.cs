using Avalonia.Controls;
using MeowGram.ViewModels.Messenger;

namespace MeowGram.Views.Messenger;

public partial class InfoPanelView : UserControl
{
    private InfoPanelViewModel? _info;

    public InfoPanelView()
    {
        InitializeComponent();
    }

    /// <summary>Крестик в шапке панели (обработчик ставит MessengerView).</summary>
    public Button Close => CloseButton;

    protected override void OnDataContextChanged(EventArgs e)
    {
        base.OnDataContextChanged(e);
        if (_info is not null)
            _info.PropertyChanged -= OnInfoChanged;
        _info = DataContext as InfoPanelViewModel;
        if (_info is not null)
            _info.PropertyChanged += OnInfoChanged;
        UpdateTabs();
    }

    private void OnInfoChanged(object? sender, System.ComponentModel.PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(InfoPanelViewModel.Tab))
            UpdateTabs();
    }

    private void UpdateTabs()
    {
        var tab = _info?.Tab ?? AttachmentTab.Files;
        TabMedia.Classes.Set("selected", tab == AttachmentTab.Media);
        TabFiles.Classes.Set("selected", tab == AttachmentTab.Files);
        TabLinks.Classes.Set("selected", tab == AttachmentTab.Links);
    }
}
