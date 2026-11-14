using TdLib;

namespace MeowGram.Core.Messaging;

/// <summary>Ход загрузки файла: <see cref="Size"/> 0 — размер ещё неизвестен.</summary>
public sealed record FileProgress(int FileId, long Downloaded, long Size, bool IsCompleted, string? LocalPath);

/// <summary>
/// Загрузка файлов TDLib (аватарки, фото, документы, голосовые). Повторный запрос того же файла ждёт ту же загрузку.
/// <see cref="Progress"/> приходит в <see cref="IMainThread"/>.
/// </summary>
public sealed class FileService(ITdExecutor td)
{
    /// <summary>Приоритеты TDLib (1–32): чем выше, тем раньше.</summary>
    public const int PriorityAvatar = 8;
    public const int PriorityThumbnail = 12;
    public const int PriorityUser = 32;

    private readonly Dictionary<int, Task<string?>> _running = [];
    private readonly Dictionary<int, string> _done = [];
    private readonly Lock _gate = new();

    public event Action<FileProgress>? Progress;

    /// <summary>Путь к уже скачанному файлу или null.</summary>
    public string? TryGetLocalPath(TdApi.File? file)
    {
        if (file is null)
            return null;

        lock (_gate)
        {
            if (_done.TryGetValue(file.Id, out var cached))
                return cached;
        }

        return IsReady(file) ? file.Local.Path : null;
    }

    /// <summary>Скачивает файл (если ещё не скачан) и возвращает локальный путь; null — файл недоступен.</summary>
    public Task<string?> DownloadAsync(TdApi.File? file, int priority)
    {
        if (file is null)
            return Task.FromResult<string?>(null);

        if (TryGetLocalPath(file) is { } ready)
            return Task.FromResult<string?>(ready);

        lock (_gate)
        {
            if (_running.TryGetValue(file.Id, out var running))
                return running;

            var task = DownloadCoreAsync(file.Id, priority);
            _running[file.Id] = task;
            return task;
        }
    }

    public Task CancelAsync(int fileId) =>
        td.ExecuteAsync(new TdApi.CancelDownloadFile { FileId = fileId, OnlyIfPending = false });

    /// <summary>Вызывать в <see cref="IMainThread"/> для каждого UpdateFile.</summary>
    public void Apply(TdApi.Update update)
    {
        if (update is not TdApi.Update.UpdateFile { File: { } file })
            return;

        var completed = IsReady(file);
        if (completed)
        {
            lock (_gate)
                _done[file.Id] = file.Local.Path;
        }

        Progress?.Invoke(new FileProgress(
            file.Id,
            file.Local?.DownloadedSize ?? 0,
            file.Size > 0 ? file.Size : file.ExpectedSize,
            completed,
            completed ? file.Local!.Path : null));
    }

    private async Task<string?> DownloadCoreAsync(int fileId, int priority)
    {
        try
        {
            // Synchronous = true: ответ приходит, когда файл скачан целиком; ход — через UpdateFile.
            var file = await td.ExecuteAsync(new TdApi.DownloadFile
            {
                FileId = fileId,
                Priority = Math.Clamp(priority, 1, 32),
                Offset = 0,
                Limit = 0,
                Synchronous = true,
            }).ConfigureAwait(false);

            if (!IsReady(file))
                return null;

            lock (_gate)
                _done[fileId] = file.Local.Path;
            return file.Local.Path;
        }
        catch (TdException)
        {
            return null;
        }
        finally
        {
            lock (_gate)
                _running.Remove(fileId);
        }
    }

    private static bool IsReady(TdApi.File file) =>
        file.Local is { IsDownloadingCompleted: true, Path.Length: > 0 } local && File.Exists(local.Path);
}
