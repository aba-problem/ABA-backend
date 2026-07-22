using Microsoft.Extensions.Caching.Memory;

namespace abaproblem.Services;

public interface ICacheService
{
    /// <summary>Cache-aside: devuelve el valor cacheado o lo calcula, cachea y devuelve.</summary>
    Task<T> GetOrCreateAsync<T>(string clave, TimeSpan duracion, Func<Task<T>> factory);
}

/// <summary>
/// Módulo 4.1 — Cachea las métricas públicas 60s en memoria para no golpear a SQL Server
/// en cada request. Módulo 6 — cada entrada declara Size explícito porque el
/// IMemoryCache raíz se registra con SizeLimit (evita que el cache crezca sin control).
/// </summary>
public sealed class CacheService : ICacheService
{
    private readonly IMemoryCache _cache;

    public CacheService(IMemoryCache cache) => _cache = cache;

    public async Task<T> GetOrCreateAsync<T>(string clave, TimeSpan duracion, Func<Task<T>> factory)
    {
        if (_cache.TryGetValue(clave, out T? cacheado) && cacheado is not null)
            return cacheado;

        var valor = await factory();
        _cache.Set(clave, valor, new MemoryCacheEntryOptions
        {
            AbsoluteExpirationRelativeToNow = duracion,
            Size = 1,
        });
        return valor;
    }
}
