using abaproblem.Contracts;

namespace abaproblem.Services;

/// <summary>
/// Módulo 8 — Orquesta "reservar en ABA_Control (PENDIENTE) → crear en MySQL real →
/// confirmar (ACTIVA/ELIMINADA)" para células socias. Solo MySQL (Aba/INVESTIGACION-PROXY-MYSQL.md
/// § 11) — a diferencia de IProvisioningOrchestrator, no hay opción de motor.
/// </summary>
public interface IPartnersProvisioningOrchestrator
{
    Task<ProvisioningResultDto> AprovisionarAsync(int celulaSociaId, CancellationToken ct = default);

    /// <summary>Control BOLA incluido: null si la base no existe o no pertenece a la célula.</summary>
    Task<BaseDatosSocioDto?> ObtenerAsync(long baseDeDatosSocioId, int celulaSociaId, CancellationToken ct = default);

    /// <summary>
    /// Desaprovisiona de verdad (DROP DATABASE + DROP USER en MySQL, no solo soft-delete en
    /// ABA_Control) — a diferencia del "desactivar" de estudiantes.
    /// </summary>
    Task DesaprovisionarAsync(long baseDeDatosSocioId, int celulaSociaId, CancellationToken ct = default);
}
