using Microsoft.Extensions.Caching.Memory;

namespace GameStore.Api.Services;

public sealed class CatalogResponseCache : IDisposable
{
    // A dedicated cache gives catalog entries their own bounded budget without
    // imposing Size requirements on unrelated framework caches.
    private readonly MemoryCache memoryCache;
    private readonly IConfiguration configuration;
    private long _version;
    // Expose utilization for diagnostics without exposing cached responses or keys.
    public int EntryCount => memoryCache.Count;

    public CatalogResponseCache(IConfiguration configuration)
    {
        this.configuration = configuration;
        memoryCache = new MemoryCache(new MemoryCacheOptions
        {
            SizeLimit = Math.Clamp(configuration.GetValue("Cache:CatalogMaxEntries", 512), 1, 4096)
        });
    }

    public async Task<T> GetOrCreateAsync<T>(string key, Func<Task<T>> factory)
        where T : notnull
    {
        var durationSeconds = configuration.GetValue("Cache:CatalogSeconds", 60);
        // Local invalidation cannot reach another replica. Bypass the catalog
        // cache in multi-instance mode so every read sees committed database data.
        if (durationSeconds <= 0 || configuration.GetValue("Deployment:MultipleInstances", false))
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

    public void Dispose() => memoryCache.Dispose();
}
