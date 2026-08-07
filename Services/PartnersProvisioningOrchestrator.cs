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
}
