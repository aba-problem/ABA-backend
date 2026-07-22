using abaproblem.Contracts;
using abaproblem.Repositories.Interfaces;

namespace abaproblem.Services;

/// <summary>
/// Implementación del patrón "reservar en ABA_Control (PENDIENTE) → crear en el motor
/// real (MySQL/SQLServer) → sp_ConfirmarAprovisionamiento(ACTIVA o ELIMINADA)".
/// </summary>
public sealed class ProvisioningOrchestrator : IProvisioningOrchestrator
{
    private readonly IProvisioningRepository _repo;
    private readonly IMySqlProvisioningService _mysql;
    private readonly ISqlServerProvisioningService _sqlServer;
    private readonly ILogger<ProvisioningOrchestrator> _logger;

    public ProvisioningOrchestrator(
        IProvisioningRepository repo,
        IMySqlProvisioningService mysql,
        ISqlServerProvisioningService sqlServer,
        ILogger<ProvisioningOrchestrator> logger)
    {
        _repo = repo;
        _mysql = mysql;
        _sqlServer = sqlServer;
        _logger = logger;
    }

    public async Task<ProvisioningResultDto> AprovisionarAsync(long usuarioId, string nombreMotor, string? ipOrigen, CancellationToken ct = default)
    {
        // 1) Reserva en ABA_Control con Estado='PENDIENTE'. Puede lanzar SpBusinessException
        //    (límite de bases excedido, motor inexistente) — se propaga tal cual.
        var reserva = await _repo.AprovisionarAsync(usuarioId, nombreMotor, ipOrigen, ct);

        try
        {
            // 2) DDL real en el motor elegido, con los valores YA sanitizados que devolvió el SP.
            if (string.Equals(reserva.Motor, "MySQL", StringComparison.OrdinalIgnoreCase))
                await _mysql.CrearBaseDeDatosAsync(reserva.NombreBD, reserva.UsuarioBD, reserva.PasswordTemporal, ct);
            else
                await _sqlServer.CrearBaseDeDatosAsync(reserva.NombreBD, reserva.UsuarioBD, reserva.PasswordTemporal, ct);
        }
        catch (Exception ex)
        {
            // 3a) Fallo: sp_ConfirmarAprovisionamiento(@Exitoso=0) → Estado='ELIMINADA'.
            //     Nunca queda un registro "activo" que no existe en el motor real.
            _logger.LogError(ex, "Fallo creando en motor {Motor} baseId={BaseId}; revirtiendo",
                reserva.Motor, reserva.BaseDeDatosId);
            await _repo.ConfirmarAsync(reserva.BaseDeDatosId, exitoso: false, ipOrigen, ct);
            throw new ProvisioningEngineException("No se pudo aprovisionar la base de datos en el motor destino.", ex);
        }

        // 3b) Éxito: confirma SOLO ahora que el motor realmente tiene la base.
        await _repo.ConfirmarAsync(reserva.BaseDeDatosId, exitoso: true, ipOrigen, ct);
        return reserva;
    }
}
