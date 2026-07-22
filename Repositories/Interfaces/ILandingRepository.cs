using abaproblem.Contracts;

namespace abaproblem.Repositories.Interfaces;

/// <summary>Módulo 4 — Lee la View agregada de métricas públicas.</summary>
public interface ILandingRepository
{
    Task<MetricasPublicasDto> ObtenerMetricasAsync(CancellationToken ct = default);
}
