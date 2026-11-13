using System.Collections.Concurrent;
using System.Text.Json;
using MeowGram.Core.Data;
using Microsoft.EntityFrameworkCore;

namespace MeowGram.Core.Settings;

/// <summary>Ключи настроек приложения.</summary>
public static class SettingKeys
{
    public const string ActiveAccountId = "accounts.active";
    public const string Theme = "ui.theme";
    public const string Accent = "ui.accent";
    public const string Language = "ui.language";
    public const string AnimationLevel = "ui.animations";
}

/// <summary>Настройки приложения (тема, язык, активный аккаунт...). Значения хранятся как JSON.</summary>
public interface ISettingsStore
{
    Task<T?> GetAsync<T>(string key, CancellationToken cancellationToken = default);

    Task SetAsync<T>(string key, T value, CancellationToken cancellationToken = default);

    Task RemoveAsync(string key, CancellationToken cancellationToken = default);
}

public sealed class SettingsStore(IDbContextFactory<AppDbContext> dbFactory) : ISettingsStore
{
    private readonly ConcurrentDictionary<string, string?> _cache = new();

    public async Task<T?> GetAsync<T>(string key, CancellationToken cancellationToken = default)
    {
        if (!_cache.TryGetValue(key, out var json))
        {
            await using var db = await dbFactory.CreateDbContextAsync(cancellationToken).ConfigureAwait(false);
            json = await db.Settings.AsNoTracking()
                .Where(s => s.Key == key)
                .Select(s => s.Value)
                .FirstOrDefaultAsync(cancellationToken)
                .ConfigureAwait(false);
            _cache[key] = json;
        }

        return json is null ? default : JsonSerializer.Deserialize<T>(json);
    }

    public async Task SetAsync<T>(string key, T value, CancellationToken cancellationToken = default)
    {
        var json = JsonSerializer.Serialize(value);

        await using var db = await dbFactory.CreateDbContextAsync(cancellationToken).ConfigureAwait(false);
        var entity = await db.Settings.FindAsync([key], cancellationToken).ConfigureAwait(false);
        if (entity is null)
            db.Settings.Add(new SettingEntity { Key = key, Value = json });
        else
            entity.Value = json;

        await db.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
        _cache[key] = json;
    }

    public async Task RemoveAsync(string key, CancellationToken cancellationToken = default)
    {
        await using var db = await dbFactory.CreateDbContextAsync(cancellationToken).ConfigureAwait(false);
        await db.Settings.Where(s => s.Key == key).ExecuteDeleteAsync(cancellationToken).ConfigureAwait(false);
        _cache[key] = null;
    }
}
