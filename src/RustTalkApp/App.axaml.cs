using Avalonia;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Markup.Xaml;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using RustTalkApp.Core;
using RustTalkApp.ViewModels;
using RustTalkApp.Views;

namespace RustTalkApp;

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
        var builder = Host.CreateApplicationBuilder();

        builder.Services.AddRustTalkCore();
        builder.Services.AddTransient<MainViewModel>();

        return builder.Build();
    }

    private void StopHost()
    {
        if (_host is null)
            return;

        _host.StopAsync(TimeSpan.FromSeconds(5)).GetAwaiter().GetResult();
        _host.Dispose();
        _host = null;
    }
}
