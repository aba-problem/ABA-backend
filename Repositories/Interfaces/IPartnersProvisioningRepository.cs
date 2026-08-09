using abaproblem.Contracts;

namespace abaproblem.Repositories.Interfaces;

/// <summary>
/// Módulo 8 — Aprovisionamiento para células socias (ABA_Control.dbo.BaseDeDatosSocio,
/// tabla separada de dbo.BaseDeDatos — ver cabecera de sql/013_celulas_socias.sql para el
/// motivo). Mismo principio que IProvisioningRepository: el backend solo dispara SPs.
/// </summary>
public interface IPartnersProvisioningRepository
{
    /// <summary>Invoca sp_AprovisionarBaseDatosSocio: reserva el registro con Estado='PENDIENTE'.</summary>
    Task<ProvisioningResultDto> AprovisionarAsync(int celulaSociaId, string nombreMotor, CancellationToken ct = default);

    /// <summary>Invoca sp_ConfirmarAprovisionamientoSocio.</summary>
    Task ConfirmarAsync(long baseDeDatosSocioId, bool exitoso, CancellationToken ct = default);

    /// <summary>Invoca sp_ObtenerBaseDatosSocioPorId (control BOLA: solo la célula dueña ve la fila).</summary>
    Task<BaseDatosSocioDto?> ObtenerPorIdAsync(long baseDeDatosSocioId, int celulaSociaId, CancellationToken ct = default);

    /// <summary>Invoca sp_DesactivarBaseDatosSocio (soft-delete del registro en ABA_Control).</summary>
    Task DesactivarAsync(long baseDeDatosSocioId, int celulaSociaId, CancellationToken ct = default);

    /// <summary>
    /// Invoca sp_RotarPasswordBaseDatosSocio: genera y persiste una contraseña nueva en
    /// ABA_Control (control BOLA + solo bases ACTIVA). Lanza SpBusinessException si la base
    /// no existe, no pertenece a la célula, o no está activa.
    /// </summary>
    Task<ResetCredencialesResultDto> RotarPasswordAsync(long baseDeDatosSocioId, int celulaSociaId, CancellationToken ct = default);

    /// <summary>Invoca sp_ListarBasesDatosSocio: todas las bases de la célula, cualquier Estado.</summary>
    Task<IReadOnlyList<BaseDatosSocioDto>> ListarAsync(int celulaSociaId, CancellationToken ct = default);
}
