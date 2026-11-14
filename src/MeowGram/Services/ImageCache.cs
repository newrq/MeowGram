using System.Collections.Concurrent;
using Avalonia.Media.Imaging;
using Avalonia.Threading;
using MeowGram.Core.Messaging;

namespace MeowGram.Services;

/// <summary>
/// Картинки из файлов TDLib (аватарки, фото): декодирование в фоне до нужной ширины и кэш.
/// Аватарки маленькие и держатся всё время; большие фото — пока их не вытеснят (<see cref="MaxLarge"/>).
/// </summary>
public static class ImageCache
{
    private const int MaxLarge = 64;

    private static readonly ConcurrentDictionary<(string Path, int Width), Task<Bitmap?>> Cache = new();
    private static readonly ConcurrentQueue<(string Path, int Width)> LargeOrder = new();

    /// <summary>Уже загруженная картинка или null (без ожидания).</summary>
    public static Bitmap? TryGet(string? path, int decodeWidth) =>
        path is not null && Cache.TryGetValue((path, decodeWidth), out var task) && task.IsCompletedSuccessfully ? task.Result : null;

    public static Task<Bitmap?> LoadAsync(string? path, int decodeWidth)
    {
        if (string.IsNullOrEmpty(path))
            return Task.FromResult<Bitmap?>(null);

        var key = (path, decodeWidth);
        var task = Cache.GetOrAdd(key, k => Task.Run(() => Decode(k.Path, k.Width)));
        if (decodeWidth > 200)
            TrimLarge(key);
        return task;
    }

    /// <summary>Скачать файл TDLib и загрузить картинку; <paramref name="apply"/> вызывается в UI-потоке.</summary>
    public static async void LoadInto(FileService files, TdLib.TdApi.File? file, int priority, int decodeWidth, Action<Bitmap> apply)
    {
        try
        {
            var path = await files.DownloadAsync(file, priority);
            if (await LoadAsync(path, decodeWidth) is { } bitmap)
                Dispatcher.UIThread.Post(() => apply(bitmap));
        }
        catch (Exception ex)
        {
            // Картинка не обязательна: без неё остаются инициалы или заглушка.
            System.Diagnostics.Debug.WriteLine($"ImageCache: {ex.Message}");
        }
    }

    private static Bitmap? Decode(string path, int width)
    {
        try
        {
            using var stream = File.OpenRead(path);
            return width > 0 ? Bitmap.DecodeToWidth(stream, width, BitmapInterpolationMode.HighQuality) : new Bitmap(stream);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException or InvalidOperationException or NotSupportedException)
        {
            return null;
        }
    }

    private static void TrimLarge((string, int) key)
    {
        LargeOrder.Enqueue(key);
        while (LargeOrder.Count > MaxLarge && LargeOrder.TryDequeue(out var old))
            Cache.TryRemove(old, out _);
    }
}
