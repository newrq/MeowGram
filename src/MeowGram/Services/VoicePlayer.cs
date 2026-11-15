using System.Runtime.InteropServices;
using Avalonia.Threading;
using LibVLCSharp.Shared;

namespace MeowGram.Services;

/// <summary>
/// Проигрыватель голосовых: один на приложение (новое голосовое останавливает предыдущее).
/// LibVLC создаётся в фоне (<see cref="WarmUp"/>): без кэша плагинов он сканирует сотни DLL по 10+ секунд,
/// и в UI-потоке это вешало окно. События VLC приходят с его потоков — переносим в UI-поток и никогда
/// не вызываем методы плеера изнутри его обработчиков (VLC так зависает). Остальные методы — только из UI-потока.
/// </summary>
public sealed class VoicePlayer : IDisposable
{
    private static readonly Lazy<VoicePlayer> LazyShared = new(() => new VoicePlayer());
    private static readonly string[] BaseOptions = ["--no-video", "--quiet"];

    private readonly Lazy<Task<Engine?>> _engine;
    private MediaPlayer? _player;
    private Media? _media;
    private float _rate = 1f;
    private object? _requested;
    private bool _disposed;

    private VoicePlayer() => _engine = new Lazy<Task<Engine?>>(() => Task.Run(CreateEngine));

    public static VoicePlayer Shared => LazyShared.Value;

    /// <summary>Что сейчас играет (ключ голосового), null — ничего.</summary>
    public object? Current { get; private set; }

    public bool IsPlaying { get; private set; }

    /// <summary>Сменилось: что играет, пауза, позиция. UI-поток.</summary>
    public event Action? StateChanged;

    /// <summary>Позиция 0..1 текущего голосового.</summary>
    public double Position { get; private set; }

    /// <summary>Начать загрузку LibVLC в фоне заранее, чтобы первое нажатие «играть» не ждало.</summary>
    public void WarmUp() => _ = _engine.Value;

    /// <summary>Играть файл. Если LibVLC ещё грузится — дождётся; играет последнее запрошенное голосовое.</summary>
    public async Task PlayAsync(object key, string path, double startAt = 0)
    {
        _requested = key;
        var engine = await _engine.Value;
        if (engine is null || _disposed || !Equals(_requested, key))
            return;

        _player ??= engine.Player;
        try
        {
            if (Equals(Current, key))
            {
                _player.SetPause(false);
                IsPlaying = true;
                StateChanged?.Invoke();
                return;
            }

            Stop();
            Current = key;
            _media = new Media(engine.Vlc, path, FromType.FromPath);
            _player.Play(_media);
            _player.SetRate(_rate);
            if (startAt > 0)
                _player.Position = (float)startAt;
            IsPlaying = true;
            Position = startAt;
            StateChanged?.Invoke();
        }
        catch (Exception ex) when (ex is VLCException or InvalidOperationException)
        {
            System.Diagnostics.Debug.WriteLine($"VoicePlayer: {ex.Message}");
            Current = null;
            IsPlaying = false;
            StateChanged?.Invoke();
        }
    }

    public void Pause()
    {
        _requested = null;
        _player?.SetPause(true);
        IsPlaying = false;
        StateChanged?.Invoke();
    }

    public void Seek(object key, double position)
    {
        if (!Equals(Current, key) || _player is null)
            return;

        _player.Position = (float)Math.Clamp(position, 0, 1);
        Position = position;
        StateChanged?.Invoke();
    }

    /// <summary>Скорость для всех голосовых (1×, 1,5×, 2×).</summary>
    public void SetRate(float rate)
    {
        _rate = rate;
        _player?.SetRate(rate);
    }

    public void Stop()
    {
        if (_player is not null && Current is not null)
            _player.Stop();
        _media?.Dispose();
        _media = null;
        Current = null;
        IsPlaying = false;
        Position = 0;
    }

    /// <summary>Фоновый поток: загрузка LibVLC и плеера. Null — LibVLC недоступен (голосовые просто не играют).</summary>
    private Engine? CreateEngine()
    {
        try
        {
            LibVLCSharp.Shared.Core.Initialize();
            string[] options = NeedsPluginCache() ? [.. BaseOptions, "--reset-plugins-cache"] : BaseOptions;
            var vlc = new LibVLC(options);
            var player = new MediaPlayer(vlc);
            player.PositionChanged += (_, e) => Dispatcher.UIThread.Post(() =>
            {
                if (Current is null)
                    return;
                Position = e.Position;
                StateChanged?.Invoke();
            });
            player.EndReached += (_, _) => Dispatcher.UIThread.Post(() =>
            {
                Stop();
                StateChanged?.Invoke();
            });
            return new Engine(vlc, player);
        }
        catch (Exception ex) when (ex is VLCException or DllNotFoundException or BadImageFormatException
                                       or InvalidOperationException or TypeInitializationException)
        {
            System.Diagnostics.Debug.WriteLine($"VoicePlayer: LibVLC init failed: {ex.Message}");
            return null;
        }
    }

    /// <summary>
    /// Windows-сборка LibVLC (NuGet) идёт без plugins.dat — без него VLC при каждом запуске заново открывает
    /// все плагины. С --reset-plugins-cache он один раз пишет кэш рядом с плагинами (если папка доступна на запись).
    /// </summary>
    private static bool NeedsPluginCache()
    {
        if (!OperatingSystem.IsWindows())
            return false;

        var arch = RuntimeInformation.ProcessArchitecture switch
        {
            Architecture.X64 => "win-x64",
            Architecture.X86 => "win-x86",
            Architecture.Arm64 => "win-arm64",
            _ => null,
        };
        if (arch is null)
            return false;

        var plugins = Path.Combine(AppContext.BaseDirectory, "libvlc", arch, "plugins");
        return Directory.Exists(plugins) && !File.Exists(Path.Combine(plugins, "plugins.dat"));
    }

    public void Dispose()
    {
        if (_disposed)
            return;

        _disposed = true;
        Stop();
        if (_engine.IsValueCreated && _engine.Value.IsCompletedSuccessfully && _engine.Value.Result is { } engine)
        {
            engine.Player.Dispose();
            engine.Vlc.Dispose();
        }
    }

    private sealed record Engine(LibVLC Vlc, MediaPlayer Player);
}
