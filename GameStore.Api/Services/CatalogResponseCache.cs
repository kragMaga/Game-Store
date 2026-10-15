using Microsoft.Extensions.Caching.Memory;

namespace GameStore.Api.Services;

public sealed class CatalogResponseCache(IMemoryCache memoryCache, IConfiguration configuration)
{
    private long _version;

    public async Task<T> GetOrCreateAsync<T>(string key, Func<Task<T>> factory)
        where T : notnull
    {
        var durationSeconds = configuration.GetValue("Cache:CatalogSeconds", 60);
        if (durationSeconds <= 0)
        {
            return await factory();
        }

        var versionedKey = $"catalog:{Volatile.Read(ref _version)}:{key}";
        var result = await memoryCache.GetOrCreateAsync(versionedKey, async entry =>
        {
            entry.AbsoluteExpirationRelativeToNow = TimeSpan.FromSeconds(durationSeconds);
            entry.Size = 1;
            return await factory();
        });

        return result ?? throw new InvalidOperationException("Catalog cache returned an empty result.");
    }

    public void Invalidate()
    {
        Interlocked.Increment(ref _version);
    }
}
