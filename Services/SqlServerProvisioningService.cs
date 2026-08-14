using System.Data;
using System.Text.RegularExpressions;
using abaproblem.Repositories.SqlServer;
using Microsoft.Data.SqlClient;

namespace abaproblem.Services;

/// <summary>
/// Ejecuta el DDL real para el motor 'SQLServer'.
///
/// En transición hacia la instancia EXTERNA de una célula socia (SqlServerExterno:AdminConnectionString,
/// ver comentario de cabecera de ISqlServerProvisioningService.cs) — mientras esa variable no esté
/// configurada, sigue usando la instancia propia (ISqlConnectionFactory, la misma que ABA_Control) tal
/// como funciona hoy en producción. El día que se configure la variable, el switch es automático, sin
/// tocar código ni redeployar: alcanza con poner el valor real en el .env de la VPS.
///
/// Límite de confianza NUEVO (mismo principio que MySqlProvisioningService): aunque
/// sp_AprovisionarBaseDatos ya generó NombreBD/UsuarioBD con un patrón fijo, esta clase los
/// re-valida antes de construir cualquier sentencia — nunca confía en que el valor que recibe sea seguro.
///
/// CREATE LOGIN no acepta una variable en la cláusula WITH PASSWORD (limitación de
/// T-SQL) — se usa el mismo patrón que sql/004_logon_trigger_sqlserver.sql: dynamic SQL
/// vía EXEC() con QUOTENAME para escapar identificador y literal de forma segura.
/// </summary>
public sealed class SqlServerProvisioningService : ISqlServerProvisioningService
{
    private static readonly Regex IdentificadorBaseDatos = new("^[a-zA-Z0-9_]{1,63}$", RegexOptions.Compiled);
    private static readonly Regex IdentificadorLogin = new("^[a-zA-Z0-9_]{1,32}$", RegexOptions.Compiled);

    private readonly string? _connectionStringExterno;
    private readonly ISqlConnectionFactory _factoryPropio;
    private readonly ILogger<SqlServerProvisioningService> _logger;

    public SqlServerProvisioningService(
        IConfiguration config, ISqlConnectionFactory factoryPropio, ILogger<SqlServerProvisioningService> logger)
    {
        _connectionStringExterno = config["SqlServerExterno:AdminConnectionString"];
        _factoryPropio = factoryPropio;
        _logger = logger;
    }

    private async Task<SqlConnection> AbrirAsync(string? catalogoDistinto, CancellationToken ct)
    {
        // Todavía no migrado: mismo comportamiento que siempre tuvo este servicio (instancia
        // propia, la de ABA_Control). ISqlConnectionFactory ya garantiza Max Pool Size, etc.
        if (string.IsNullOrWhiteSpace(_connectionStringExterno))
        {
            return catalogoDistinto is not null
                ? await _factoryPropio.AbrirAsync(catalogoDistinto, ct)
                : await _factoryPropio.AbrirAsync(ct);
        }

        // Migrado: instancia externa de la célula socia.
        var cs = catalogoDistinto is not null
            // SqlConnectionStringBuilder (API estructurada, no concatenación) para cambiar de
            // catálogo sin arriesgar el resto de la cadena.
            ? new SqlConnectionStringBuilder(_connectionStringExterno) { InitialCatalog = catalogoDistinto }.ConnectionString
            : _connectionStringExterno;

        var conn = new SqlConnection(cs);
        await conn.OpenAsync(ct);
        return conn;
    }

    public async Task CrearBaseDeDatosAsync(string nombreBD, string usuarioBD, string password, CancellationToken ct = default)
    {
        Validar(nombreBD, IdentificadorBaseDatos, nameof(nombreBD));
        Validar(usuarioBD, IdentificadorLogin, nameof(usuarioBD));

        await using var conn = await AbrirAsync(null, ct); // conexión de servidor (propia o externa, ver AbrirAsync)

        var creoLogin = false;
        var creoBaseDatos = false;
        try
        {
            await using (var cmd = new SqlCommand(
                "DECLARE @sql NVARCHAR(MAX) = N'CREATE LOGIN ' + QUOTENAME(@UsuarioBD) + " +
                "N' WITH PASSWORD = ' + QUOTENAME(@Password, '''') + N', CHECK_POLICY = ON;'; " +
                "EXEC (@sql);", conn))
            {
                cmd.Parameters.Add("@UsuarioBD", SqlDbType.NVarChar, 128).Value = usuarioBD;
                cmd.Parameters.Add("@Password", SqlDbType.NVarChar, 128).Value = password;
                await cmd.ExecuteNonQueryAsync(ct);
                creoLogin = true;
            }

            await using (var cmd = new SqlCommand(
                "DECLARE @sql NVARCHAR(MAX) = N'CREATE DATABASE ' + QUOTENAME(@NombreBD) + N';'; EXEC (@sql);", conn))
            {
                cmd.Parameters.Add("@NombreBD", SqlDbType.NVarChar, 128).Value = nombreBD;
                await cmd.ExecuteNonQueryAsync(ct);
                creoBaseDatos = true;
            }

            // Conexión SEPARADA con Initial Catalog = la BD nueva (nunca "USE" sobre la
            // conexión compartida — dejaría el contexto sucio para quien reutilice el pool).
            await using (var connBd = await AbrirAsync(nombreBD, ct))
            await using (var cmd = new SqlCommand(
                "DECLARE @sql NVARCHAR(MAX) = " +
                "N'CREATE USER ' + QUOTENAME(@UsuarioBD) + N' FOR LOGIN ' + QUOTENAME(@UsuarioBD) + N'; ' + " +
                "N'ALTER ROLE db_owner ADD MEMBER ' + QUOTENAME(@UsuarioBD) + N';'; " +
                "EXEC (@sql);", connBd))
            {
                // Control 2.1 — db_owner ÚNICAMENTE de su propia BD (nunca de ABA_Control ni de otras).
                cmd.Parameters.Add("@UsuarioBD", SqlDbType.NVarChar, 128).Value = usuarioBD;
                await cmd.ExecuteNonQueryAsync(ct);
            }
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Fallo creando en SQL Server base={Base} login={Login}; intentando limpieza", nombreBD, usuarioBD);
            await LimpiarParcialAsync(conn, nombreBD, usuarioBD, creoBaseDatos, creoLogin, ct);
            throw;
        }
    }

    private async Task LimpiarParcialAsync(
        SqlConnection conn, string nombreBD, string usuarioBD, bool creoBaseDatos, bool creoLogin, CancellationToken ct)
    {
        if (creoBaseDatos)
        {
            try
            {
                await using var cmd = new SqlCommand(
                    "DECLARE @sql NVARCHAR(MAX) = N'ALTER DATABASE ' + QUOTENAME(@NombreBD) + " +
                    "N' SET SINGLE_USER WITH ROLLBACK IMMEDIATE; DROP DATABASE ' + QUOTENAME(@NombreBD) + N';'; " +
                    "EXEC (@sql);", conn);
                cmd.Parameters.Add("@NombreBD", SqlDbType.NVarChar, 128).Value = nombreBD;
                await cmd.ExecuteNonQueryAsync(ct);
            }
            catch (Exception limpiezaEx)
            {
                _logger.LogError(limpiezaEx, "No se pudo limpiar la base huérfana {Base} tras fallo parcial", nombreBD);
            }
        }

        if (creoLogin)
        {
            try
            {
                await using var cmd = new SqlCommand(
                    "DECLARE @sql NVARCHAR(MAX) = N'DROP LOGIN ' + QUOTENAME(@UsuarioBD) + N';'; EXEC (@sql);", conn);
                cmd.Parameters.Add("@UsuarioBD", SqlDbType.NVarChar, 128).Value = usuarioBD;
                await cmd.ExecuteNonQueryAsync(ct);
            }
            catch (Exception limpiezaEx)
            {
                _logger.LogError(limpiezaEx, "No se pudo limpiar el login huérfano {Login} tras fallo parcial", usuarioBD);
            }
        }
    }

    private static void Validar(string valor, Regex patron, string nombreParametro)
    {
        if (string.IsNullOrEmpty(valor) || !patron.IsMatch(valor))
            throw new ArgumentException(
                $"Identificador inválido para uso en DDL de SQL Server: '{valor}'.", nombreParametro);
    }
}
