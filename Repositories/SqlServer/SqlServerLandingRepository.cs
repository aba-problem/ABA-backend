using abaproblem.Contracts;
using abaproblem.Repositories.Interfaces;
using Microsoft.Data.SqlClient;

namespace abaproblem.Repositories.SqlServer;

/// <summary>
/// Módulo 4 — Lee la View agregada vw_MetricasPublicas. Consulta estática sin parámetros
/// de cliente (sin superficie de inyección) contra una vista ya agregada por el motor.
/// </summary>
public sealed class SqlServerLandingRepository : ILandingRepository
{
    private readonly ISqlConnectionFactory _factory;

    public SqlServerLandingRepository(ISqlConnectionFactory factory) => _factory = factory;

    public async Task<MetricasPublicasDto> ObtenerMetricasAsync(CancellationToken ct = default)
    {
        await using var conn = await _factory.AbrirAsync(ct);
        await using var cmd = new SqlCommand("SELECT * FROM dbo.vw_MetricasPublicas", conn);

        await using var reader = await cmd.ExecuteReaderAsync(ct);
        if (!await reader.ReadAsync(ct))
            throw new InvalidOperationException("vw_MetricasPublicas no devolvió filas.");

        return new MetricasPublicasDto
        {
            TotalUsuarios = reader.GetInt32(reader.GetOrdinal("TotalUsuarios")),
            TotalBasesCreadas = reader.GetInt32(reader.GetOrdinal("TotalBasesCreadas")),
            BasesActivas = reader.GetInt32(reader.GetOrdinal("BasesActivas")),
            TotalLogins = reader.GetInt32(reader.GetOrdinal("TotalLogins")),
            UsuariosActivos30Dias = reader.GetInt32(reader.GetOrdinal("UsuariosActivos30Dias")),
            DisponibilidadPorcentaje = reader.GetDecimal(reader.GetOrdinal("DisponibilidadPorcentaje")),
        };
    }
}
