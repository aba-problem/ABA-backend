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
    private readonly IMongoProvisioningService _mongo;
    private readonly ILogger<ProvisioningOrchestrator> _logger;

    public ProvisioningOrchestrator(
        IProvisioningRepository repo,
        IMySqlProvisioningService mysql,
        ISqlServerProvisioningService sqlServer,
        IMongoProvisioningService mongo,
        ILogger<ProvisioningOrchestrator> logger)
    {
        _repo = repo;
        _mysql = mysql;
        _sqlServer = sqlServer;
        _mongo = mongo;
        _logger = logger;
    }

    public async Task<ProvisioningResultDto> AprovisionarAsync(long usuarioId, string nombreMotor, string? ipOrigen, CancellationToken ct = default)
    {
        // 1) Reserva en ABA_Control con Estado='PENDIENTE'. Puede lanzar SpBusinessException
        //    (límite de bases excedido, motor inexistente) — se propaga tal cual.
        var reserva = await _repo.AprovisionarAsync(usuarioId, nombreMotor, ipOrigen, ct);

        // MongoDB es distinto de MySQL/SQLServer: el usuario/password/host REALES los decide
        // el proveedor externo, no SQL Server (que solo generó valores de reserva descartables
        // en el paso 1). Por eso confirma con sp_ConfirmarAprovisionamientoExterno en vez de
        // sp_ConfirmarAprovisionamiento — necesita persistir esos valores reales.
        if (string.Equals(reserva.Motor, "MongoDB", StringComparison.OrdinalIgnoreCase))
            return await AprovisionarMongoAsync(reserva, ipOrigen, ct);

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

    private async Task<ProvisioningResultDto> AprovisionarMongoAsync(ProvisioningResultDto reserva, string? ipOrigen, CancellationToken ct)
    {
        MongoProvisioningResult resultado;
        try
        {
            // El nombre de usuario que le pedimos al proveedor es el que ya generó el SP en
            // la reserva (único, atado a UsuarioId) — el proveedor puede devolver uno distinto
            // si lo normaliza; usamos SIEMPRE lo que el proveedor confirma como real.
            resultado = await _mongo.CrearBaseDeDatosAsync(reserva.UsuarioBD, ct);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Fallo creando en motor MongoDB baseId={BaseId}; revirtiendo", reserva.BaseDeDatosId);
            await _repo.ConfirmarAsync(reserva.BaseDeDatosId, exitoso: false, ipOrigen, ct);
            throw ex as ProvisioningEngineException
                  ?? new ProvisioningEngineException("No se pudo aprovisionar la base de datos en el motor destino.", ex);
        }

        try
        {
            await _repo.ConfirmarExternoAsync(
                reserva.BaseDeDatosId, exitoso: true,
                resultado.Database, resultado.Username, resultado.Password, resultado.Host, resultado.Puerto, resultado.ExternalId,
                ipOrigen, ct);
        }
        catch (Exception ex)
        {
            // La base YA existe en el proveedor pero no pudimos confirmarla en ABA_Control —
            // mejor un huérfano detectable (limpieza manual/job futuro) que perder el registro
            // de que existe. Se intenta eliminar en el proveedor para no dejar el huérfano ahí también.
            _logger.LogError(ex, "Base MongoDB creada (externalId={ExternalId}) pero falló la confirmación en ABA_Control",
                resultado.ExternalId);
            await _mongo.EliminarBaseDeDatosAsync(resultado.ExternalId, ct);
            await _repo.ConfirmarAsync(reserva.BaseDeDatosId, exitoso: false, ipOrigen, ct);
            throw new ProvisioningEngineException("No se pudo aprovisionar la base de datos en el motor destino.", ex);
        }

        return reserva with
        {
            NombreBD = resultado.Database,
            UsuarioBD = resultado.Username,
            Host = resultado.Host,
            Puerto = resultado.Puerto,
            PasswordTemporal = resultado.Password,
        };
    }
}
