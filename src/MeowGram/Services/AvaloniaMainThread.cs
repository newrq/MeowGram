using Avalonia.Threading;
using MeowGram.Core.Messaging;

namespace MeowGram.Services;

/// <summary>Состояние чатов ядра живёт в UI-потоке: обновления TDLib переносятся сюда по порядку.</summary>
public sealed class AvaloniaMainThread : IMainThread
{
    public void Post(Action action) => Dispatcher.UIThread.Post(action, DispatcherPriority.Background);
}
