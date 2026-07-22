namespace abaproblem.Services;

/// <summary>
/// sql/005_logon_trigger_mysql.sql deja explícito que MySQL Community no puede consultar
/// ABA_Control en vivo (motores distintos, sin Linked Server posible), así que el backend
/// debe replicar la whitelist hacia la tabla espejo aba_seguridad.whitelist_ip cada vez que
/// cambie en ABA_Control para un usuario con bases en MySQL. Se invoca tras cada login
/// (después de sp_RegistrarIpUsuario).
/// </summary>
public interface IMySqlWhitelistSyncService
{
    Task SincronizarAsync(long usuarioId, CancellationToken ct = default);
}
