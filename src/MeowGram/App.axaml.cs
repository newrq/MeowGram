using Avalonia;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Markup.Xaml;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Configuration;
using MeowGram.Core;
using MeowGram.Core.Telegram;
using MeowGram.ViewModels;
using MeowGram.Views;

namespace MeowGram;

public partial class App : Application
{
    private IHost? _host;

    /// <summary>DI-контейнер приложения. Доступен после OnFrameworkInitializationCompleted.</summary>
    public IServiceProvider Services => _host?.Services
        ?? throw new InvalidOperationException("Host is not built yet.");

    public override void Initialize()
    {
        AvaloniaXamlLoader.Load(this);
    }

    public override void OnFrameworkInitializationCompleted()
    {
        _host = CreateHost();
        _host.Start();

        if (ApplicationLifetime is IClassicDesktopStyleApplicationLifetime desktop)
        {
            desktop.MainWindow = new MainWindow
            {
                DataContext = Services.GetRequiredService<MainViewModel>(),
            };
            desktop.Exit += (_, _) => StopHost();
        }

        base.OnFrameworkInitializationCompleted();
    }

    private static IHost CreateHost()
    {
        var builder = Host.CreateApplicationBuilder(new HostApplicationBuilderSettings
        {
            // appsettings*.json лежат рядом с exe, а не в текущей папке запуска.
            ContentRootPath = AppContext.BaseDirectory,
        });

        builder.Configuration
            .AddJsonFile("appsettings.Local.json", optional: true, reloadOnChange: false)
            .AddEnvironmentVariables(prefix: "MEOWGRAM_");

        builder.Services.AddMeowGramCore();
        builder.Services.Configure<TelegramOptions>(builder.Configuration.GetSection(TelegramOptions.SectionName));
        builder.Services.AddTransient<MainViewModel>();

        return builder.Build();
    }

    private void StopHost()
    {
        if (_host is null)
            return;

        // Ядро освобождается асинхронно (TDLib нужно корректно закрыть), синхронный Dispose бросил бы исключение.
        // Внутри ядра везде ConfigureAwait(false), поэтому ожидание из UI-потока не блокируется.
        _host.StopAsync(TimeSpan.FromSeconds(10)).GetAwaiter().GetResult();
        if (_host is IAsyncDisposable asyncDisposable)
            asyncDisposable.DisposeAsync().AsTask().GetAwaiter().GetResult();
        else
            _host.Dispose();
        _host = null;
    }
}
