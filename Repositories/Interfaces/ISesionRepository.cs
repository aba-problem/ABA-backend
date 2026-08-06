using abaproblem.Contracts;

namespace abaproblem.Repositories.Interfaces;

/// <summary>Historial de acceso (login/registro/whitelist de IP) del usuario autenticado.</summary>
public interface ISesionRepository
{
    /// <summary>Invoca sp_ListarSesionesUsuario — siempre filtrado por el usuario del JWT.</summary>
    Task<IReadOnlyList<SesionRegistroDto>> ListarAsync(long usuarioId, CancellationToken ct = default);
}
