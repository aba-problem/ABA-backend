using abaproblem.Repositories.Interfaces;
using MySqlConnector;

namespace abaproblem.Services;

/// <summary>
/// Se invoca de forma SÍNCRONA una vez por login exitoso (ver
/// AuthController.RegistrarWhitelistSinFallarLoginAsync), inmediatamente después de
/// sp_RegistrarIpUsuario. NO es un job periódico — no necesita (ni debe) registrarse
/// como IHostedService/BackgroundService: la sincronización tiene que reflejar la IP
/// recién validada en el mismo momento del login, no en el próximo ciclo de un timer.
/// </summary>
public sealed class MySqlWhitelistSyncService : IMySqlWhitelistSyncService
{
    private readonly IIpWhitelistRepository _ipRepo;
    private readonly string _connectionString;
    private readonly ILogger<MySqlWhitelistSyncService> _logger;

    public MySqlWhitelistSyncService(
        IIpWhitelistRepository ipRepo, IConfiguration config, ILogger<MySqlWhitelistSyncService> logger)
    {
        _ipRepo = ipRepo;
        _connectionString = config["MySql:AdminConnectionString"]
            ?? throw new InvalidOperationException("MySql:AdminConnectionString no configurada.");
        _logger = logger;
    }

    public async Task SincronizarAsync(long usuarioId, CancellationToken ct = default)
    {
        var loginsMySql = await _ipRepo.ListarUsuarioBDMySqlAsync(usuarioId, ct);
        if (loginsMySql.Count == 0)
        {
            // Antes esto retornaba en silencio — indistinguible en el log de "nunca se llamó".
            _logger.LogInformation(
                "Sin bases MySQL ACTIVA para usuarioId={UsuarioId}; nada que sincronizar en whitelist_ip",
                usuarioId);
            return;
        }

        var ipsActivas = await _ipRepo.ListarIpsActivasAsync(usuarioId, ct);

        MySqlConnection conn;
        try
        {
            conn = new MySqlConnection(_connectionString);
            await conn.OpenAsync(ct);
        }
        catch (Exception ex)
        {
            // Antes: si esto fallaba, no quedaba log DESDE este servicio (solo el catch
            // genérico del caller en AuthController). Ahora se ve exactamente aquí.
            _logger.LogError(ex,
                "No se pudo abrir conexión a MySQL para sincronizar whitelist (usuarioId={UsuarioId}, logins={Logins})",
                usuarioId, string.Join(",", loginsMySql));
            return;
        }

        await using (conn)
        {
            var sincronizados = 0;
            foreach (var usuarioBd in loginsMySql)
            {
                try
                {
                    // Reconciliación completa: activa las IPs vigentes, desactiva el resto —
                    // refleja exactamente lo que sp_RegistrarIpUsuario ya decidió en ABA_Control
                    // (incluyendo el housekeeping de "solo las 5 IPs más recientes").
                    await using (var cmdDesactivar = new MySqlCommand(
                        "UPDATE aba_seguridad.whitelist_ip SET activo = 0 " +
                        "WHERE usuario_bd = @usuario AND direccion_ip NOT IN (" +
                        string.Join(",", ipsActivas.Select((_, i) => $"@ip{i}")) + ");", conn))
                    {
                        cmdDesactivar.Parameters.AddWithValue("@usuario", usuarioBd);
                        for (var i = 0; i < ipsActivas.Count; i++)
                            cmdDesactivar.Parameters.AddWithValue($"@ip{i}", ipsActivas[i]);

                        // NOT IN () con lista vacía es SQL inválido — sin IPs activas, desactiva todo.
                        if (ipsActivas.Count == 0)
                        {
                            cmdDesactivar.CommandText =
                                "UPDATE aba_seguridad.whitelist_ip SET activo = 0 WHERE usuario_bd = @usuario;";
                        }
                        await cmdDesactivar.ExecuteNonQueryAsync(ct);
                    }

                    foreach (var ip in ipsActivas)
                    {
                        await using var cmdUpsert = new MySqlCommand(
                            "INSERT INTO aba_seguridad.whitelist_ip (usuario_bd, direccion_ip, activo) " +
                            "VALUES (@usuario, @ip, 1) " +
                            "ON DUPLICATE KEY UPDATE activo = 1, actualizado_en = CURRENT_TIMESTAMP;", conn);
                        cmdUpsert.Parameters.AddWithValue("@usuario", usuarioBd);
                        cmdUpsert.Parameters.AddWithValue("@ip", ip);
                        await cmdUpsert.ExecuteNonQueryAsync(ct);
                    }

                    sincronizados++;
                }
                catch (Exception ex)
                {
                    // No debe romper el login: si el espejo MySQL falla, el logon trigger de
                    // SQL Server (004) sigue protegiendo ese motor; solo se pierde el
                    // equivalente para bases MySQL hasta el próximo login/sincronización.
                    _logger.LogError(ex, "No se pudo sincronizar whitelist MySQL para usuarioBD={UsuarioBD}", usuarioBd);
                }
            }

            // Antes: un sync 100% exitoso no dejaba NINGÚN rastro en el log.
            _logger.LogInformation(
                "Whitelist MySQL sincronizada usuarioId={UsuarioId} logins={Sincronizados}/{Total} ipsActivas={IpsActivas}",
                usuarioId, sincronizados, loginsMySql.Count, ipsActivas.Count);
        }
    }
}
