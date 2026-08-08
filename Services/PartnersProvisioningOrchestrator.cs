using abaproblem.Contracts;
using abaproblem.Repositories.Interfaces;

namespace abaproblem.Services;

public sealed class PartnersProvisioningOrchestrator : IPartnersProvisioningOrchestrator
{
    private readonly IPartnersProvisioningRepository _repo;
    private readonly IMySqlProvisioningService _mysql;
    private readonly ILogger<PartnersProvisioningOrchestrator> _logger;

    public PartnersProvisioningOrchestrator(
        IPartnersProvisioningRepository repo,
        IMySqlProvisioningService mysql,
        ILogger<PartnersProvisioningOrchestrator> logger)
    {
        _repo = repo;
        _mysql = mysql;
        _logger = logger;
    }

    public async Task<ProvisioningResultDto> AprovisionarAsync(int celulaSociaId, CancellationToken ct = default)
    {
        // 1) Reserva en ABA_Control con Estado='PENDIENTE'. Puede lanzar SpBusinessException
        //    (célula inactiva, límite de bases excedido).
        var reserva = await _repo.AprovisionarAsync(celulaSociaId, "MySQL", ct);

        try
        {
            // 2) DDL real en MySQL, con los valores YA sanitizados que devolvió el SP.
            await _mysql.CrearBaseDeDatosAsync(reserva.NombreBD, reserva.UsuarioBD, reserva.PasswordTemporal, ct);
        }
        catch (Exception ex)
        {
            // 3a) Fallo: confirmar(exitoso=false) → Estado='ELIMINADA'. Nunca queda un
            //     registro "activo" que no existe en el motor real.
            _logger.LogError(ex, "Fallo creando en MySQL (socia) baseId={BaseId}; revirtiendo", reserva.BaseDeDatosId);
            await _repo.ConfirmarAsync(reserva.BaseDeDatosId, exitoso: false, ct);
            throw new ProvisioningEngineException("No se pudo aprovisionar la base de datos en el motor destino.", ex);
        }

        // 3b) Éxito: confirma SOLO ahora que MySQL realmente tiene la base.
        await _repo.ConfirmarAsync(reserva.BaseDeDatosId, exitoso: true, ct);
        return reserva;
    }

    public Task<BaseDatosSocioDto?> ObtenerAsync(long baseDeDatosSocioId, int celulaSociaId, CancellationToken ct = default)
        => _repo.ObtenerPorIdAsync(baseDeDatosSocioId, celulaSociaId, ct);

    public async Task DesaprovisionarAsync(long baseDeDatosSocioId, int celulaSociaId, CancellationToken ct = default)
    {
        // Resuelve NombreBD/UsuarioBD reales y confirma pertenencia (control BOLA) antes de
        // tocar el motor — nunca confiar en que el llamador mande el nombre real de la BD.
        var actual = await _repo.ObtenerPorIdAsync(baseDeDatosSocioId, celulaSociaId, ct)
            ?? throw new SpBusinessException(50105, "La base de datos no existe o no pertenece a esta celula.");

        await _mysql.EliminarBaseDeDatosAsync(actual.NombreBD, actual.UsuarioBD, ct);
        await _repo.DesactivarAsync(baseDeDatosSocioId, celulaSociaId, ct);
    }

    public async Task<ResetCredencialesResultDto> RotarPasswordAsync(long baseDeDatosSocioId, int celulaSociaId, CancellationToken ct = default)
    {
        // 1) ABA_Control genera y persiste la contraseña nueva (fuente de verdad) — puede
        //    lanzar SpBusinessException (no existe, no es de esta célula, o no está ACTIVA).
        var resultado = await _repo.RotarPasswordAsync(baseDeDatosSocioId, celulaSociaId, ct);

        try
        {
            // 2) Aplica la contraseña nueva en el motor real.
            await _mysql.RotarPasswordAsync(resultado.UsuarioBD, resultado.PasswordNueva, ct);
        }
        catch (Exception ex)
        {
            // A diferencia de AprovisionarAsync, acá no hay fila que "abandonar": la base ya
            // existe y sigue funcionando con la contraseña VIEJA en MySQL (este paso todavía
            // no la tocó). ABA_Control queda con una contraseña que MySQL nunca recibió —
            // se autocorrige solo en el próximo POST .../credenciales/reset (vuelve a generar
            // y sincronizar), por eso no se intenta un rollback del cifrado acá.
            _logger.LogError(ex, "Fallo aplicando password nueva en MySQL (socia) baseId={BaseId}", baseDeDatosSocioId);
            throw new ProvisioningEngineException("No se pudo rotar la contraseña en el motor destino.", ex);
        }

        return resultado;
    }
}
