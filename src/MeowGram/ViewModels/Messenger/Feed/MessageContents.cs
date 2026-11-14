using System.Diagnostics;
using Avalonia.Media.Imaging;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using MeowGram.Core.Messaging;
using MeowGram.Localization;
using MeowGram.Services;
using TdLib;

namespace MeowGram.ViewModels.Messenger.Feed;

/// <summary>Кусок форматированного текста сообщения: стиль и ссылка (если есть).</summary>
public sealed record RichSegment(string Text, bool Bold = false, bool Italic = false, bool Code = false,
    bool Strike = false, bool Underline = false, string? Url = null)
{
    public bool IsLink => Url is not null;
}

/// <summary>Содержимое пузыря. Конкретный тип выбирает шаблон в MessageTemplates.axaml.</summary>
public abstract partial class MessageContentViewModel : ObservableObject
{
    /// <summary>Сообщение, которому принадлежит содержимое: шаблоны берут у него время, статус и направление.</summary>
    public MessageViewModel Owner { get; internal set; } = null!;

    /// <summary>Текст для «Копировать» и цитаты.</summary>
    public virtual string PlainText => string.Empty;

    /// <summary>Пузырь без фона (стикер).</summary>
    public virtual bool IsBare => false;

    public static MessageContentViewModel Create(MessagingContext context, TdApi.Message message) => message.Content switch
    {
        TdApi.MessageContent.MessageText t => new TextContentViewModel(t.Text, t.LinkPreview),
        TdApi.MessageContent.MessagePhoto p => new PhotoContentViewModel(context, p.Photo, p.Caption),
        TdApi.MessageContent.MessageVideo v => new PhotoContentViewModel(context, v.Video?.Thumbnail, v.Video?.Width ?? 0, v.Video?.Height ?? 0, v.Caption, isVideo: true, file: v.Video?.Video_),
        TdApi.MessageContent.MessageAnimation a => new PhotoContentViewModel(context, a.Animation?.Thumbnail, a.Animation?.Width ?? 0, a.Animation?.Height ?? 0, a.Caption, isVideo: true, file: a.Animation?.Animation_),
        TdApi.MessageContent.MessageVoiceNote v => new VoiceContentViewModel(context, message, v),
        TdApi.MessageContent.MessageDocument d => new FileContentViewModel(context, d.Document?.Document_, d.Document?.FileName, d.Caption),
        TdApi.MessageContent.MessageAudio a => new FileContentViewModel(context, a.Audio?.Audio_, a.Audio?.FileName, a.Caption),
        TdApi.MessageContent.MessageSticker s => new StickerContentViewModel(context, s.Sticker),
        _ => new TextContentViewModel(new TdApi.FormattedText { Text = ChatText.Preview(context.Store, message).Text, Entities = [] }, null, isPlaceholder: true),
    };
}

/// <summary>Текст с форматированием, ссылками и (необязательно) карточкой превью ссылки.</summary>
public sealed class TextContentViewModel : MessageContentViewModel
{
    public TextContentViewModel(TdApi.FormattedText? text, TdApi.LinkPreview? preview, bool isPlaceholder = false)
    {
        Text = text?.Text ?? string.Empty;
        Segments = Parse(text);
        IsPlaceholder = isPlaceholder;
        if (preview is not null)
        {
            PreviewSite = preview.SiteName ?? string.Empty;
            PreviewTitle = preview.Title ?? string.Empty;
            PreviewDescription = preview.Description?.Text ?? string.Empty;
            PreviewUrl = preview.Url;
        }
    }

    public string Text { get; }

    public IReadOnlyList<RichSegment> Segments { get; }

    /// <summary>Тип сообщения, который не отображается полноценно (опрос, контакт...): текст-описание курсивом.</summary>
    public bool IsPlaceholder { get; }

    public string PreviewSite { get; } = string.Empty;

    public string PreviewTitle { get; } = string.Empty;

    public string PreviewDescription { get; } = string.Empty;

    public string? PreviewUrl { get; }

    public bool HasPreview => PreviewTitle.Length > 0 || PreviewDescription.Length > 0;

    public bool HasPreviewSite => PreviewSite.Length > 0;

    public bool HasPreviewTitle => PreviewTitle.Length > 0;

    public bool HasPreviewDescription => PreviewDescription.Length > 0;

    public override string PlainText => Text;

    /// <summary>Разбивает текст по сущностям TDLib (смещения в UTF-16, как в .NET).</summary>
    public static IReadOnlyList<RichSegment> Parse(TdApi.FormattedText? formatted)
    {
        var text = formatted?.Text ?? string.Empty;
        var entities = formatted?.Entities ?? [];
        if (entities.Length == 0)
            return text.Length == 0 ? [] : [new RichSegment(text)];

        // Границы всех сущностей → отрезки, у каждого — набор стилей покрывающих его сущностей.
        var cuts = new SortedSet<int> { 0, text.Length };
        foreach (var e in entities)
        {
            cuts.Add(Math.Clamp(e.Offset, 0, text.Length));
            cuts.Add(Math.Clamp(e.Offset + e.Length, 0, text.Length));
        }

        var result = new List<RichSegment>();
        var points = cuts.ToArray();
        for (var i = 0; i < points.Length - 1; i++)
        {
            var start = points[i];
            var end = points[i + 1];
            if (end <= start)
                continue;

            var piece = text[start..end];
            var seg = new RichSegment(piece);
            foreach (var e in entities.Where(e => e.Offset <= start && e.Offset + e.Length >= end))
            {
                seg = e.Type switch
                {
                    TdApi.TextEntityType.TextEntityTypeBold => seg with { Bold = true },
                    TdApi.TextEntityType.TextEntityTypeItalic => seg with { Italic = true },
                    TdApi.TextEntityType.TextEntityTypeUnderline => seg with { Underline = true },
                    TdApi.TextEntityType.TextEntityTypeStrikethrough => seg with { Strike = true },
                    TdApi.TextEntityType.TextEntityTypeCode or TdApi.TextEntityType.TextEntityTypePre
                        or TdApi.TextEntityType.TextEntityTypePreCode => seg with { Code = true },
                    TdApi.TextEntityType.TextEntityTypeUrl => seg with { Url = Normalize(text.Substring(e.Offset, e.Length)) },
                    TdApi.TextEntityType.TextEntityTypeTextUrl u => seg with { Url = u.Url },
                    TdApi.TextEntityType.TextEntityTypeEmailAddress => seg with { Url = "mailto:" + text.Substring(e.Offset, e.Length) },
                    TdApi.TextEntityType.TextEntityTypeMention => seg with { Url = "https://t.me/" + text.Substring(e.Offset, e.Length).TrimStart('@') },
                    _ => seg,
                };
            }

            result.Add(seg);
        }

        return result;
    }

    private static string Normalize(string url) =>
        url.Contains("://", StringComparison.Ordinal) ? url : "https://" + url;
}

/// <summary>Фото, видео или GIF: превью с правильными пропорциями; видео — с кнопкой «играть» поверх.</summary>
public sealed partial class PhotoContentViewModel : MessageContentViewModel
{
    private const double MaxWidth = 340;
    private const double MaxHeight = 360;
    private readonly MessagingContext _context;
    private readonly TdApi.File? _openFile;

    public PhotoContentViewModel(MessagingContext context, TdApi.Photo? photo, TdApi.FormattedText? caption)
        : this(context, null, 0, 0, caption, isVideo: false, file: null)
    {
        var sizes = photo?.Sizes ?? [];
        // Для пузыря хватает размера ~800px; самый большой открывается по клику.
        var best = sizes.Where(s => s.Width <= 1280).OrderByDescending(s => s.Width).FirstOrDefault() ?? sizes.LastOrDefault();
        _openFile = sizes.OrderByDescending(s => s.Width).FirstOrDefault()?.Photo;
        if (best is not null)
        {
            SetSize(best.Width, best.Height);
            Load(best.Photo);
        }
    }

    public PhotoContentViewModel(MessagingContext context, TdApi.Thumbnail? thumbnail, int width, int height,
        TdApi.FormattedText? caption, bool isVideo, TdApi.File? file)
    {
        _context = context;
        _openFile = file;
        IsVideo = isVideo;
        Caption = new TextContentViewModel(caption, null);
        SetSize(width > 0 ? width : thumbnail?.Width ?? 320, height > 0 ? height : thumbnail?.Height ?? 240);
        if (thumbnail is not null)
            Load(thumbnail.File);
    }

    [ObservableProperty]
    public partial Bitmap? Image { get; private set; }

    public double Width { get; private set; }

    public double Height { get; private set; }

    public bool IsVideo { get; }

    public TextContentViewModel Caption { get; }

    public bool HasCaption => Caption.Text.Length > 0;

    public override string PlainText => Caption.Text;

    /// <summary>Открыть оригинал в программе по умолчанию.</summary>
    [RelayCommand]
    private async Task OpenAsync()
    {
        if (await _context.Files.DownloadAsync(_openFile, FileService.PriorityUser) is { } path)
            FileContentViewModel.OpenInShell(path);
    }

    private void SetSize(int width, int height)
    {
        if (width <= 0 || height <= 0)
            (width, height) = (320, 240);
        var scale = Math.Min(MaxWidth / width, MaxHeight / height);
        scale = Math.Min(scale, 1.5);
        Width = Math.Max(120, width * scale);
        Height = Math.Max(80, height * scale);
    }

    private void Load(TdApi.File? file) =>
        ImageCache.LoadInto(_context.Files, file, FileService.PriorityThumbnail, 680, bitmap => Image = bitmap);
}

/// <summary>Документ или аудио: карточка с иконкой, именем, размером; клик скачивает и открывает.</summary>
public sealed partial class FileContentViewModel : MessageContentViewModel, IDisposable
{
    private readonly MessagingContext _context;
    private readonly TdApi.File? _file;

    public FileContentViewModel(MessagingContext context, TdApi.File? file, string? name, TdApi.FormattedText? caption)
    {
        _context = context;
        _file = file;
        FileName = string.IsNullOrWhiteSpace(name) ? Loc.Instance["Content_File"] : name;
        Size = file is null ? string.Empty : ChatText.FileSize(file.Size > 0 ? file.Size : file.ExpectedSize);
        Caption = new TextContentViewModel(caption, null);
        IsDownloaded = context.Files.TryGetLocalPath(file) is not null;
        _context.Files.Progress += OnProgress;
    }

    public string FileName { get; }

    public string Size { get; }

    /// <summary>Расширение для плашки в инфо-панели: «PDF», «WEBP».</summary>
    public string Extension => Path.GetExtension(FileName).TrimStart('.').ToUpperInvariant() is { Length: > 0 and <= 4 } ext ? ext : "FILE";

    public TextContentViewModel Caption { get; }

    public bool HasCaption => Caption.Text.Length > 0;

    [ObservableProperty]
    public partial bool IsDownloading { get; private set; }

    [ObservableProperty]
    public partial bool IsDownloaded { get; private set; }

    /// <summary>0..1 во время загрузки.</summary>
    [ObservableProperty]
    public partial double Progress { get; private set; }

    public override string PlainText => Caption.Text.Length > 0 ? Caption.Text : FileName;

    [RelayCommand]
    private async Task OpenAsync()
    {
        if (_file is null)
            return;

        if (IsDownloading)
        {
            try
            {
                await _context.Files.CancelAsync(_file.Id);
            }
            catch (Exception ex) when (ex is TdLib.TdException or InvalidOperationException)
            {
                // Загрузка уже закончилась или сессия закрыта.
            }

            IsDownloading = false;
            return;
        }

        IsDownloading = !IsDownloaded;
        var path = await _context.Files.DownloadAsync(_file, FileService.PriorityUser);
        IsDownloading = false;
        IsDownloaded = path is not null;
        if (path is not null)
            OpenInShell(path);
    }

    public static void OpenInShell(string path)
    {
        try
        {
            Process.Start(new ProcessStartInfo(path) { UseShellExecute = true });
        }
        catch (Exception ex) when (ex is System.ComponentModel.Win32Exception or InvalidOperationException or FileNotFoundException)
        {
            Debug.WriteLine($"Не удалось открыть {path}: {ex.Message}");
        }
    }

    private void OnProgress(FileProgress progress)
    {
        if (_file is null || progress.FileId != _file.Id)
            return;

        Progress = progress.Size > 0 ? (double)progress.Downloaded / progress.Size : 0;
        if (progress.IsCompleted)
        {
            IsDownloading = false;
            IsDownloaded = true;
        }
    }

    public void Dispose() => _context.Files.Progress -= OnProgress;
}

/// <summary>Стикер: картинка без пузыря (WebP), для анимированных — превью или эмодзи.</summary>
public sealed partial class StickerContentViewModel : MessageContentViewModel
{
    public StickerContentViewModel(MessagingContext context, TdApi.Sticker? sticker)
    {
        Emoji = sticker?.Emoji ?? string.Empty;
        var file = sticker?.Format is TdApi.StickerFormat.StickerFormatWebp ? sticker.Sticker_ : sticker?.Thumbnail?.File;
        if (file is not null)
            ImageCache.LoadInto(context.Files, file, FileService.PriorityThumbnail, 320, bitmap => Image = bitmap);
    }

    public string Emoji { get; }

    [ObservableProperty]
    public partial Bitmap? Image { get; private set; }

    public override bool IsBare => true;

    public override string PlainText => Emoji;
}
