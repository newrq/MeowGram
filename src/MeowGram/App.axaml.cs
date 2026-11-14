using Avalonia;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Markup.Xaml;
using MeowGram.Core;
using MeowGram.Core.Messaging;
using MeowGram.Core.Settings;
using MeowGram.Core.Telegram;
using MeowGram.Localization;
using MeowGram.Services;
using MeowGram.ViewModels;
using MeowGram.ViewModels.Login;
using MeowGram.Views;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;

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
        // Хост (БД, TDLib) — только в настоящем окне приложения; в тестах без экрана (headless) его нет.
        if (ApplicationLifetime is IClassicDesktopStyleApplicationLifetime desktop)
        {
            _host = CreateHost();
            _host.Start();

            ApplySavedPreferences(Services);

            var main = Services.GetRequiredService<MainViewModel>();
            desktop.MainWindow = new MainWindow { DataContext = main };
            main.Initialize();
            desktop.Exit += (_, _) => StopHost();
        }

        base.OnFrameworkInitializationCompleted();
    }

    /// <summary>Тема, акцент, анимации и язык — до показа окна, чтобы не мелькнула тема по умолчанию.</summary>
    private static void ApplySavedPreferences(IServiceProvider services)
    {
        var settings = services.GetRequiredService<ISettingsStore>();
        var theme = services.GetRequiredService<ThemeService>();

        // Чтение БД — в пуле потоков: синхронное ожидание из UI-потока с захватом его контекста зависло бы.
        var (preferences, language) = Task.Run(async () =>
            (await theme.LoadAsync().ConfigureAwait(false), await Loc.LoadLanguageAsync(settings).ConfigureAwait(false)))
            .GetAwaiter().GetResult();

        theme.Apply(preferences);
        Loc.Instance.SetLanguage(language);
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

        // До ядра: оно регистрирует поток состояния чатов, только если его не задали.
        builder.Services.AddSingleton<IMainThread, AvaloniaMainThread>();
        builder.Services.AddMeowGramCore();
        builder.Services.Configure<TelegramOptions>(builder.Configuration.GetSection(TelegramOptions.SectionName));
        builder.Services.AddSingleton<ThemeService>();
        builder.Services.AddSingleton<MainViewModel>();
        builder.Services.AddTransient<LoginPanelsViewModel>();
        builder.Services.AddTransient<LoginViewModel>();

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
