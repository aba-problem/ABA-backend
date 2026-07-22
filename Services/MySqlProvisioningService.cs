using System.Text.RegularExpressions;
using MySqlConnector;

namespace abaproblem.Services;

/// <summary>
/// Módulo 2 (auditoría de cierre) — Ejecuta el DDL real en MySQL: CREATE DATABASE,
/// CREATE USER, GRANT/REVOKE acotados a la base del tenant.
///
/// Límite de confianza NUEVO: aunque sp_AprovisionarBaseDatos ya valida los nombres
/// con la misma regex, esta clase NUNCA asume que el valor que recibe es seguro —
/// se re-valida aquí antes de construir cualquier sentencia SQL. Los identificadores
/// (nombre de BD / usuario) SOLO se interpolan después de pasar la regex; la
/// contraseña SIEMPRE viaja como parámetro de MySqlCommand, nunca concatenada.
///
/// La cuenta administrativa (MySql:AdminConnectionString) debe tener EXCLUSIVAMENTE:
///   CREATE, DROP, CREATE USER, GRANT OPTION, SELECT ON information_schema.*
/// Nunca SUPER ni GRANT ALL PRIVILEGES ON *.* — ver README § Módulo 2.
/// </summary>
public sealed class MySqlProvisioningService : IMySqlProvisioningService
{
    private static readonly Regex IdentificadorValido = new("^[a-zA-Z0-9_]{1,30}$", RegexOptions.Compiled);

    private readonly string _connectionString;
    private readonly ILogger<MySqlProvisioningService> _logger;

    public MySqlProvisioningService(IConfiguration config, ILogger<MySqlProvisioningService> logger)
    {
        _connectionString = config["MySql:AdminConnectionString"]
            ?? throw new InvalidOperationException("MySql:AdminConnectionString no configurada.");
        _logger = logger;
    }

    public async Task CrearBaseDeDatosAsync(string nombreBaseDatos, string usuarioBaseDatos, string password, CancellationToken ct = default)
    {
        ValidarIdentificador(nombreBaseDatos, nameof(nombreBaseDatos));
        ValidarIdentificador(usuarioBaseDatos, nameof(usuarioBaseDatos));

        await using var conn = new MySqlConnection(_connectionString);
        await conn.OpenAsync(ct);

        var creoBaseDatos = false;
        var creoUsuario = false;
        try
        {
            // Identificadores ya validados por regex (solo [a-zA-Z0-9_]) → seguros para interpolar.
            await using (var cmd = new MySqlCommand(
                $"CREATE DATABASE `{nombreBaseDatos}` CHARACTER SET utf8mb4 COLLATE utf8mb4_unicode_ci;", conn))
            {
                await cmd.ExecuteNonQueryAsync(ct);
                creoBaseDatos = true;
            }

            await using (var cmd = new MySqlCommand(
                $"CREATE USER '{usuarioBaseDatos}'@'%' IDENTIFIED BY @password;", conn))
            {
                cmd.Parameters.AddWithValue("@password", password); // NUNCA concatenado
                await cmd.ExecuteNonQueryAsync(ct);
                creoUsuario = true;
            }

            // Control 2.1 — permisos acotados SOLO a esta base, nunca ALL PRIVILEGES ON *.*.
            await using (var cmd = new MySqlCommand(
                $"GRANT ALL PRIVILEGES ON `{nombreBaseDatos}`.* TO '{usuarioBaseDatos}'@'%';", conn))
            {
                await cmd.ExecuteNonQueryAsync(ct);
            }
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Fallo creando en MySQL base={Base} usuario={Usuario}; intentando limpieza",
                nombreBaseDatos, usuarioBaseDatos);
            await LimpiarParcialAsync(conn, nombreBaseDatos, usuarioBaseDatos, creoBaseDatos, creoUsuario, ct);
            throw;
        }
    }

    public async Task<int> ObtenerEspacioUsadoMbAsync(string nombreBaseDatos, CancellationToken ct = default)
    {
        ValidarIdentificador(nombreBaseDatos, nameof(nombreBaseDatos));

        await using var conn = new MySqlConnection(_connectionString);
        await conn.OpenAsync(ct);

        await using var cmd = new MySqlCommand(
            "SELECT ROUND(SUM(data_length + index_length) / 1024 / 1024) AS UsadoMb " +
            "FROM information_schema.tables WHERE table_schema = @schema;", conn);
        cmd.Parameters.AddWithValue("@schema", nombreBaseDatos); // valor, no identificador → parametrizable

        var resultado = await cmd.ExecuteScalarAsync(ct);
        return resultado is DBNull or null ? 0 : Convert.ToInt32(resultado);
    }

    public async Task RevocarEscrituraAsync(string nombreBaseDatos, string usuarioBaseDatos, CancellationToken ct = default)
    {
        ValidarIdentificador(nombreBaseDatos, nameof(nombreBaseDatos));
        ValidarIdentificador(usuarioBaseDatos, nameof(usuarioBaseDatos));

        await using var conn = new MySqlConnection(_connectionString);
        await conn.OpenAsync(ct);
        await using var cmd = new MySqlCommand(
            $"REVOKE INSERT, UPDATE, DELETE, CREATE, ALTER, DROP, INDEX, REFERENCES " +
            $"ON `{nombreBaseDatos}`.* FROM '{usuarioBaseDatos}'@'%';", conn);
        await cmd.ExecuteNonQueryAsync(ct);
    }

    public async Task RestaurarEscrituraAsync(string nombreBaseDatos, string usuarioBaseDatos, CancellationToken ct = default)
    {
        ValidarIdentificador(nombreBaseDatos, nameof(nombreBaseDatos));
        ValidarIdentificador(usuarioBaseDatos, nameof(usuarioBaseDatos));

        await using var conn = new MySqlConnection(_connectionString);
        await conn.OpenAsync(ct);
        await using var cmd = new MySqlCommand(
            $"GRANT INSERT, UPDATE, DELETE, CREATE, ALTER, DROP, INDEX, REFERENCES " +
            $"ON `{nombreBaseDatos}`.* TO '{usuarioBaseDatos}'@'%';", conn);
        await cmd.ExecuteNonQueryAsync(ct);
    }

    private async Task LimpiarParcialAsync(
        MySqlConnection conn, string nombreBaseDatos, string usuarioBaseDatos,
        bool creoBaseDatos, bool creoUsuario, CancellationToken ct)
    {
        if (creoUsuario)
        {
            try
            {
                await using var cmd = new MySqlCommand($"DROP USER IF EXISTS '{usuarioBaseDatos}'@'%';", conn);
                await cmd.ExecuteNonQueryAsync(ct);
            }
            catch (Exception limpiezaEx)
            {
                _logger.LogError(limpiezaEx, "No se pudo limpiar el usuario huérfano {Usuario} tras fallo parcial", usuarioBaseDatos);
            }
        }

        if (creoBaseDatos)
        {
            try
            {
                await using var cmd = new MySqlCommand($"DROP DATABASE IF EXISTS `{nombreBaseDatos}`;", conn);
                await cmd.ExecuteNonQueryAsync(ct);
            }
            catch (Exception limpiezaEx)
            {
                _logger.LogError(limpiezaEx, "No se pudo limpiar la base huérfana {Base} tras fallo parcial", nombreBaseDatos);
            }
        }
    }

    /// <summary>
    /// Límite de confianza nuevo (auditoría de cierre): re-valida el identificador AUNQUE
    /// ya venga validado por el SP. Nunca se interpola un valor sin pasar por aquí.
    /// </summary>
    private static void ValidarIdentificador(string valor, string nombreParametro)
    {
        if (string.IsNullOrEmpty(valor) || !IdentificadorValido.IsMatch(valor))
            throw new ArgumentException(
                $"Identificador inválido para uso en DDL de MySQL: '{valor}'.", nombreParametro);
    }
}
