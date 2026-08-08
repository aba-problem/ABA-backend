using MySqlConnector;

namespace abaproblem.Services;

/// <summary>
/// La interfaz admin de ProxySQL (puerto 6032) habla el protocolo MySQL, así que se
/// administra con el mismo driver (MySqlConnector) que MySqlProvisioningService usa contra
/// el motor real — son conexiones y credenciales completamente separadas (ProxySql:AdminConnectionString,
/// cuenta `radmin`, nunca `aba_provisioner`).
///
/// `REPLACE INTO` (no `INSERT ... ON DUPLICATE KEY UPDATE`): la tabla mysql_users vive en el
/// almacenamiento interno de ProxySQL (SQLite embebido expuesto sobre el wire protocol de
/// MySQL), y `REPLACE INTO` es la forma idiomática de upsert documentada por ProxySQL —
/// no toda la sintaxis extendida de MySQL está soportada ahí.
/// </summary>
public sealed class ProxySqlAdminService : IProxySqlAdminService
{
    private readonly string _connectionString;
    private readonly ILogger<ProxySqlAdminService> _logger;

    public ProxySqlAdminService(IConfiguration config, ILogger<ProxySqlAdminService> logger)
    {
        _connectionString = config["ProxySql:AdminConnectionString"]
            ?? throw new InvalidOperationException("ProxySql:AdminConnectionString no configurada.");
        _logger = logger;
    }

    public async Task RegistrarUsuarioAsync(string usuario, string password, CancellationToken ct = default)
    {
        await using var conn = new MySqlConnection(_connectionString);
        await conn.OpenAsync(ct);

        await using (var cmd = new MySqlCommand(
            "REPLACE INTO mysql_users(username, password, default_hostgroup) VALUES (@usuario, @password, 0);", conn))
        {
            cmd.Parameters.AddWithValue("@usuario", usuario);
            cmd.Parameters.AddWithValue("@password", password); // NUNCA concatenado
            await cmd.ExecuteNonQueryAsync(ct);
        }

        await AplicarCambiosAsync(conn, ct);
        _logger.LogInformation("Usuario {Usuario} registrado en ProxySQL", usuario);
    }

    public async Task EliminarUsuarioAsync(string usuario, CancellationToken ct = default)
    {
        await using var conn = new MySqlConnection(_connectionString);
        await conn.OpenAsync(ct);

        await using (var cmd = new MySqlCommand("DELETE FROM mysql_users WHERE username = @usuario;", conn))
        {
            cmd.Parameters.AddWithValue("@usuario", usuario);
            await cmd.ExecuteNonQueryAsync(ct);
        }

        await AplicarCambiosAsync(conn, ct);
        _logger.LogInformation("Usuario {Usuario} eliminado de ProxySQL", usuario);
    }

    private static async Task AplicarCambiosAsync(MySqlConnection conn, CancellationToken ct)
    {
        // Dos comandos separados, no uno solo con ";" — el parser admin de ProxySQL no
        // soporta multi-statement en una sola query (a diferencia de un MySQL real).
        // LOAD aplica el cambio a las conexiones nuevas; SAVE lo persiste en disco para que
        // sobreviva un restart del contenedor de ProxySQL.
        await using (var cmd = new MySqlCommand("LOAD MYSQL USERS TO RUNTIME;", conn))
            await cmd.ExecuteNonQueryAsync(ct);

        await using (var cmd = new MySqlCommand("SAVE MYSQL USERS TO DISK;", conn))
            await cmd.ExecuteNonQueryAsync(ct);
    }
}
