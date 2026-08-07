namespace abaproblem.Services;

/// <summary>
/// ProxySQL no delega autenticación a MySQL: valida usuario+contraseña contra su propia
/// tabla `mysql_users` (interfaz admin, puerto 6032). Sin este registro, un estudiante recién
/// aprovisionado en MySQL no puede conectar por el puerto público (que hoy es ProxySQL, no
/// MySQL directo — ver Aba/INFRAESTRUCTURA.md § 8).
/// </summary>
public interface IProxySqlAdminService
{
    /// <summary>Registra (o actualiza la contraseña de) un usuario en mysql_users y aplica el cambio.</summary>
    Task RegistrarUsuarioAsync(string usuario, string password, CancellationToken ct = default);

    /// <summary>Quita un usuario de mysql_users y aplica el cambio.</summary>
    Task EliminarUsuarioAsync(string usuario, CancellationToken ct = default);
}
