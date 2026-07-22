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
    /// </summary>
    Task<UsuarioDto> ObtenerOCrearAsync(ExternalLoginInfo info, string? ipOrigen, CancellationToken ct = default);
}
