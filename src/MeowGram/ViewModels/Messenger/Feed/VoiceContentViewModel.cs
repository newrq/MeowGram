using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using MeowGram.Core.Messaging;
using MeowGram.Services;
using TdLib;

namespace MeowGram.ViewModels.Messenger.Feed;

/// <summary>Овал волны-подушечек: высота 6–20px, отступ (между четвёрками шире), проиграно ли.</summary>
public sealed partial class BeanViewModel(double height, double margin) : ObservableObject
{
    public double Height { get; } = height;

    public double Margin { get; } = margin;

    [ObservableProperty]
    public partial bool IsPlayed { get; set; }
}

/// <summary>
/// Голосовое: кнопка-лапка (играть/пауза), волна из овалов по четыре, время, скорость в лапках, след «не прослушано».
/// Проигрывает <see cref="VoicePlayer.Shared"/>; файл скачивается из TDLib при первом нажатии.
/// </summary>
public sealed partial class VoiceContentViewModel : MessageContentViewModel, IDisposable
{
    public const int BeanCount = 24;
    private static readonly float[] Rates = [1f, 1.5f, 2f];
    private static int _rateIndex;

    private readonly MessagingContext _context;
    private readonly long _chatId;
    private readonly long _messageId;
    private readonly TdApi.File? _file;
    private readonly int _duration;
    private readonly bool _isOutgoing;

    public VoiceContentViewModel(MessagingContext context, TdApi.Message message, TdApi.MessageContent.MessageVoiceNote voice)
    {
        _context = context;
        _chatId = message.ChatId;
        _messageId = message.Id;
        _isOutgoing = message.IsOutgoing;
        _file = voice.VoiceNote?.Voice;
        _duration = voice.VoiceNote?.Duration ?? 0;
        IsListened = voice.IsListened || message.IsOutgoing;
        Caption = new TextContentViewModel(voice.Caption, null);
        Beans = BuildBeans(voice.VoiceNote?.Waveform ?? []);
        VoicePlayer.Shared.StateChanged += OnPlayerChanged;
        UpdateFromPlayer();
    }

    public IReadOnlyList<BeanViewModel> Beans { get; }

    public TextContentViewModel Caption { get; }

    public bool HasCaption => Caption.Text.Length > 0;

    [ObservableProperty]
    public partial bool IsPlaying { get; private set; }

    [ObservableProperty]
    public partial bool IsLoading { get; private set; }

    /// <summary>Своё не помечается; чужое — след лапки, пока не прослушано.</summary>
    [ObservableProperty]
    public partial bool IsListened { get; set; }

    [ObservableProperty]
    public partial string TimeText { get; private set; } = string.Empty;

    /// <summary>Сколько лапок у кнопки скорости: 1, 2 (1,5×) или 3 (2×).</summary>
    [ObservableProperty]
    public partial int SpeedPaws { get; private set; } = 1;

    [ObservableProperty]
    public partial string SpeedText { get; private set; } = "1×";

    public bool ShowUnlistened => !IsListened && !_isOutgoing;

    public bool ShowPaw2 => SpeedPaws >= 2;

    public bool ShowPaw3 => SpeedPaws >= 3;

    partial void OnSpeedPawsChanged(int value)
    {
        OnPropertyChanged(nameof(ShowPaw2));
        OnPropertyChanged(nameof(ShowPaw3));
    }

    public override string PlainText => Caption.Text;

    private bool IsCurrent => Equals(VoicePlayer.Shared.Current, Key);

    private (long, long) Key => (_chatId, _messageId);

    [RelayCommand]
    private async Task PlayPauseAsync()
    {
        var player = VoicePlayer.Shared;
        if (IsCurrent && player.IsPlaying)
        {
            player.Pause();
            return;
        }

        IsLoading = true;
        var path = await _context.Files.DownloadAsync(_file, FileService.PriorityUser);
        IsLoading = false;
        if (path is null)
            return;

        player.SetRate(Rates[_rateIndex]);
        player.Play(Key, path);
        if (!IsListened)
        {
            IsListened = true;
            OnPropertyChanged(nameof(ShowUnlistened));
            try
            {
                await _context.Messages.OpenContentAsync(_chatId, _messageId);
            }
            catch (Exception ex) when (ex is TdException or InvalidOperationException)
            {
                // Отметка «прослушано» не критична.
            }
        }
    }

    /// <summary>Клик по лапкам скорости: 1× → 1,5× → 2× по кругу, для всех голосовых.</summary>
    [RelayCommand]
    private void CycleSpeed()
    {
        _rateIndex = (_rateIndex + 1) % Rates.Length;
        VoicePlayer.Shared.SetRate(Rates[_rateIndex]);
        UpdateFromPlayer();
    }

    /// <summary>Клик по волне: перемотка (0..1).</summary>
    public void Seek(double position) => VoicePlayer.Shared.Seek(Key, position);

    private void OnPlayerChanged() => UpdateFromPlayer();

    private void UpdateFromPlayer()
    {
        var player = VoicePlayer.Shared;
        var current = IsCurrent;
        IsPlaying = current && player.IsPlaying;
        var position = current ? player.Position : 0;
        var played = (int)Math.Round(position * BeanCount);
        for (var i = 0; i < Beans.Count; i++)
            Beans[i].IsPlayed = current && i < played;

        TimeText = current
            ? $"{ChatText.Duration((int)(position * _duration))} / {ChatText.Duration(_duration)}"
            : ChatText.Duration(_duration);

        SpeedPaws = _rateIndex + 1;
        SpeedText = Rates[_rateIndex] switch
        {
            1f => "1×",
            1.5f => Localization.Loc.Instance.Language == "ru" ? "1,5×" : "1.5×",
            _ => "2×",
        };
        OnPropertyChanged(nameof(ShowUnlistened));
    }

    /// <summary>Волна TDLib — 5-битные значения подряд; сводим к 24 овалам высотой 6–20px.</summary>
    public static IReadOnlyList<BeanViewModel> BuildBeans(byte[] waveform)
    {
        var values = Unpack(waveform);
        var result = new BeanViewModel[BeanCount];
        var max = values.Length > 0 ? Math.Max(1, values.Max()) : 1;
        for (var i = 0; i < BeanCount; i++)
        {
            double amplitude;
            if (values.Length == 0)
            {
                amplitude = 0.35;
            }
            else
            {
                var from = i * values.Length / BeanCount;
                var to = Math.Max(from + 1, (i + 1) * values.Length / BeanCount);
                amplitude = values[from..Math.Min(to, values.Length)].Average() / max;
            }

            result[i] = new BeanViewModel(Math.Round(6 + amplitude * 14), i == 0 ? 0 : i % 4 == 0 ? 7 : 3);
        }

        return result;
    }

    private static int[] Unpack(byte[] data)
    {
        var count = data.Length * 8 / 5;
        var values = new int[count];
        for (var i = 0; i < count; i++)
        {
            var bit = i * 5;
            var b = bit / 8;
            var shift = bit % 8;
            var word = data[b] | (b + 1 < data.Length ? data[b + 1] << 8 : 0);
            values[i] = (word >> shift) & 0x1F;
        }

        return values;
    }

    public void Dispose() => VoicePlayer.Shared.StateChanged -= OnPlayerChanged;
}
