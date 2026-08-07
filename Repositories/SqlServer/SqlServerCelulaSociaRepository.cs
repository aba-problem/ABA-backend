using System.Data;
using abaproblem.Contracts;
using abaproblem.Repositories.Interfaces;
using Microsoft.Data.SqlClient;

namespace abaproblem.Repositories.SqlServer;

/// <summary>
/// Módulo 8 — Implementación SQL Server del repositorio de células socias (ABA_Control).
/// SOLO invoca sp_ValidarApiKeyCelula con parámetros tipados. Ninguna concatenación de SQL.
/// </summary>
public sealed class SqlServerCelulaSociaRepository : ICelulaSociaRepository
{
    private readonly ISqlConnectionFactory _factory;

    public SqlServerCelulaSociaRepository(ISqlConnectionFactory factory) => _factory = factory;

    public async Task<CelulaSociaDto?> ValidarApiKeyAsync(byte[] apiKeyHash, CancellationToken ct = default)
    {
        await using var conn = await _factory.AbrirAsync(ct);
        await using var cmd = new SqlCommand("dbo.sp_ValidarApiKeyCelula", conn)
        {
            CommandType = CommandType.StoredProcedure,
        };

        cmd.Parameters.Add("@ApiKeyHash", SqlDbType.VarBinary, 64).Value = apiKeyHash;

        await using var reader = await cmd.ExecuteReaderAsync(ct);
        if (!await reader.ReadAsync(ct))
            return null; // key inexistente o célula desactivada — sp_ValidarApiKeyCelula no devuelve filas

        return new CelulaSociaDto
        {
            CelulaId = reader.GetInt32(reader.GetOrdinal("CelulaId")),
            NombreCelula = reader.GetString(reader.GetOrdinal("NombreCelula")),
            Prefijo = reader.GetString(reader.GetOrdinal("Prefijo")),
        };
    }
}
