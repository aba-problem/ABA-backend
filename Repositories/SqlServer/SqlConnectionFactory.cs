using Microsoft.Data.SqlClient;

namespace abaproblem.Repositories.SqlServer;

public interface ISqlConnectionFactory
{
    Task<SqlConnection> AbrirAsync(CancellationToken ct = default);

    /// <summary>
    /// Abre una conexión a un catálogo (base de datos) distinto en el MISMO servidor —
    /// necesario para el motor 'SQLServer' del aprovisionamiento (crear el usuario dentro
    /// de la BD recién creada, no en ABA_Control). Usa SqlConnectionStringBuilder (API
    /// estructurada, no concatenación) para no arriesgar el pool de la conexión por defecto:
    /// un `USE` ejecutado sobre la conexión compartida de ABA_Control dejaría el contexto
    /// "sucio" para quien reutilice esa conexión del pool después.
    /// </summary>
    Task<SqlConnection> AbrirAsync(string baseDeDatos, CancellationToken ct = default);
}

/// <summary>
/// Módulo 6 — Presupuesto de memoria. El pool de conexiones se acota EXPLÍCITAMENTE en la
/// connection string (Max Pool Size=20; Min Pool Size=2). Aquí solo validamos que el límite
/// esté presente para no dejar el pool crecer sin control y reventar el presupuesto de RAM.
/// </summary>
public sealed class SqlConnectionFactory : ISqlConnectionFactory
{
    private readonly string _connectionString;

    public SqlConnectionFactory(IConfiguration config)
    {
        var cs = config.GetConnectionString("Default")
                 ?? throw new InvalidOperationException("ConnectionStrings:Default no configurada.");

        // Verificación defensiva del control de RAM: el pool debe estar acotado.
        if (!cs.Contains("Max Pool Size", StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException(
                "La connection string debe fijar 'Max Pool Size' (Módulo 6: presupuesto de RAM).");

        _connectionString = cs;
    }

    public async Task<SqlConnection> AbrirAsync(CancellationToken ct = default)
    {
        var conn = new SqlConnection(_connectionString);
        await conn.OpenAsync(ct);
        return conn;
    }

    public async Task<SqlConnection> AbrirAsync(string baseDeDatos, CancellationToken ct = default)
    {
        var builder = new SqlConnectionStringBuilder(_connectionString) { InitialCatalog = baseDeDatos };
        var conn = new SqlConnection(builder.ConnectionString);
        await conn.OpenAsync(ct);
        return conn;
    }
}
