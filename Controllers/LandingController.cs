using abaproblem.Repositories.Interfaces;
using abaproblem.Services;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.RateLimiting;

namespace abaproblem.Controllers;

/// <summary>
/// Módulo 4 — Landing Page / Métricas Públicas. Único conjunto de endpoints SIN
/// autenticación (control 4.1): tratado como superficie de ataque de alto riesgo,
/// por eso rate limit agresivo + resultado cacheado 60s.
/// </summary>
[ApiController]
[AllowAnonymous]
public sealed class LandingController : ControllerBase
{
    private readonly ILandingRepository _repo;
    private readonly ICacheService _cache;

    public LandingController(ILandingRepository repo, ICacheService cache)
    {
        _repo = repo;
        _cache = cache;
    }

    /// <summary>
    /// Estadísticas públicas agregadas. Solo números — nunca datos identificables de
    /// usuarios individuales. Cacheado 60s en memoria para proteger a SQL Server y el
    /// presupuesto de RAM/CPU (Módulo 6) de cálculos agregados en cada request público.
    /// </summary>
    [HttpGet("stats")]
    [EnableRateLimiting("landing")] // Sliding Window agresivo por IP (control 4.1)
    public async Task<IActionResult> Stats(CancellationToken ct)
    {
        var metricas = await _cache.GetOrCreateAsync(
            "landing:stats",
            TimeSpan.FromSeconds(60),
            () => _repo.ObtenerMetricasAsync(ct));

        return Ok(metricas);
    }
}
