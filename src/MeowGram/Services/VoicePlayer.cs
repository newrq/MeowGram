using Avalonia.Threading;
using LibVLCSharp.Shared;

namespace MeowGram.Services;

/// <summary>
/// Проигрыватель голосовых: один на приложение (новое голосовое останавливает предыдущее). LibVLC загружается
/// при первом проигрывании. События VLC приходят с его потоков — переносим в UI-поток и никогда не вызываем
/// методы плеера изнутри его обработчиков (VLC так зависает).
/// </summary>
public sealed class VoicePlayer : IDisposable
{
    private static readonly Lazy<VoicePlayer> LazyShared = new(() => new VoicePlayer());

    private LibVLC? _vlc;
    private MediaPlayer? _player;
    private Media? _media;
    private float _rate = 1f;

    public static VoicePlayer Shared => LazyShared.Value;

    /// <summary>Что сейчас играет (ключ голосового), null — ничего.</summary>
    public object? Current { get; private set; }

    public bool IsPlaying { get; private set; }

    /// <summary>Сменилось: что играет, пауза, позиция. UI-поток.</summary>
    public event Action? StateChanged;

    /// <summary>Позиция 0..1 текущего голосового.</summary>
    public double Position { get; private set; }

    public void Play(object key, string path, double startAt = 0)
    {
        try
        {
            EnsurePlayer();
            if (Equals(Current, key) && _player is { } same)
            {
                same.SetPause(false);
                IsPlaying = true;
                StateChanged?.Invoke();
                return;
            }

            Stop();
            Current = key;
            _media = new Media(_vlc!, path, FromType.FromPath);
            _player!.Play(_media);
            _player.SetRate(_rate);
            if (startAt > 0)
                _player.Position = (float)startAt;
            IsPlaying = true;
            Position = startAt;
            StateChanged?.Invoke();
        }
        catch (Exception ex) when (ex is VLCException or DllNotFoundException or InvalidOperationException)
        {
            System.Diagnostics.Debug.WriteLine($"VoicePlayer: {ex.Message}");
            Current = null;
            IsPlaying = false;
            StateChanged?.Invoke();
        }
    }

    public void Pause()
    {
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

    private void EnsurePlayer()
    {
        if (_player is not null)
            return;

        LibVLCSharp.Shared.Core.Initialize();
        _vlc = new LibVLC("--no-video", "--quiet");
        _player = new MediaPlayer(_vlc);
        _player.PositionChanged += (_, e) => Dispatcher.UIThread.Post(() =>
        {
            Position = e.Position;
            StateChanged?.Invoke();
        });
        _player.EndReached += (_, _) => Dispatcher.UIThread.Post(() =>
        {
            Stop();
            StateChanged?.Invoke();
        });
    }

    public void Dispose()
    {
        Stop();
        _player?.Dispose();
        _vlc?.Dispose();
    }
}
