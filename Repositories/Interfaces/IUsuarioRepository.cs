using abaproblem.Contracts;

namespace abaproblem.Repositories.Interfaces;

/// <summary>
/// Módulo 1 — Acceso a datos de usuario. El backend NO contiene lógica: esta interfaz
/// solo describe la invocación de sp_CrearUsuario (ABA_Control). Toda la regla (upsert,
/// no duplicar, timestamps) vive en el SP.
/// </summary>
public interface IUsuarioRepository
{
    /// <summary>
    /// Invoca sp_CrearUsuario (upsert dentro del SP, nunca en el backend).
    /// Si (Proveedor, ProveedorUsuarioId) ya existe, reutiliza el registro y actualiza último login.
    /// <paramref name="userAgent"/> es puramente informativo — se guarda en el detalle del
    /// evento LOGIN de Auditoria para mostrarlo en Registros de sesión, ninguna regla depende de él.
    /// </summary>
    Task<UsuarioDto> ObtenerOCrearAsync(ExternalLoginInfo info, string? ipOrigen, string? userAgent, CancellationToken ct = default);
}
