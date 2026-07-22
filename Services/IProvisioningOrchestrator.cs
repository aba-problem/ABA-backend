using abaproblem.Contracts;

namespace abaproblem.Services;

/// <summary>
/// Coordina la reserva en ABA_Control con la creación real en el motor elegido
/// (MySQL o SQLServer). Esto NO es lógica de negocio (nombres/límites siguen
/// decidiéndose en los SPs): es la orquestación técnica de dos sistemas que no
/// comparten una transacción distribuida, con su compensación si el segundo paso falla.
/// </summary>
public interface IProvisioningOrchestrator
{
    Task<ProvisioningResultDto> AprovisionarAsync(long usuarioId, string nombreMotor, string? ipOrigen, CancellationToken ct = default);
}
