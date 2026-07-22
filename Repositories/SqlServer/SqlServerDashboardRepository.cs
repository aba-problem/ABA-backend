using System.Data;
using abaproblem.Contracts;
using abaproblem.Repositories.Interfaces;
using Microsoft.Data.SqlClient;

namespace abaproblem.Repositories.SqlServer;

/// <summary>
/// Módulo 3 — Implementación SQL Server del dashboard (ABA_Control). SOLO invoca SPs;
/// el control BOLA (dueño real) vive dentro de sp_ObtenerCredencialesBaseDatos.
/// </summary>
public sealed class SqlServerDashboardRepository : IDashboardRepository
{
    private readonly ISqlConnectionFactory _factory;

    public SqlServerDashboardRepository(ISqlConnectionFactory factory) => _factory = factory;

    public async Task<IReadOnlyList<DashboardItemDto>> ListarAsync(long usuarioId, CancellationToken ct = default)
    {
        await using var conn = await _factory.AbrirAsync(ct);
        await using var cmd = new SqlCommand("dbo.sp_ListarBasesDatosUsuario", conn)
        {
            CommandType = CommandType.StoredProcedure,
        };
        cmd.Parameters.Add("@UsuarioId", SqlDbType.Int).Value = (int)usuarioId;

        var resultado = new List<DashboardItemDto>();
        await using var reader = await cmd.ExecuteReaderAsync(ct);
        while (await reader.ReadAsync(ct))
        {
            resultado.Add(new DashboardItemDto
            {
                Id = reader.GetInt32(reader.GetOrdinal("Id")),
                NombreBD = reader.GetString(reader.GetOrdinal("NombreBD")),
                UsuarioBD = reader.GetString(reader.GetOrdinal("UsuarioBD")),
                Host = reader.GetString(reader.GetOrdinal("Host")),
                Puerto = reader.GetInt32(reader.GetOrdinal("Puerto")),
                Motor = reader.GetString(reader.GetOrdinal("Motor")),
                Estado = reader.GetString(reader.GetOrdinal("Estado")),
                FechaCreacion = reader.GetDateTime(reader.GetOrdinal("FechaCreacion")),
                UltimaActividad = reader.IsDBNull(reader.GetOrdinal("UltimaActividad"))
                    ? null
                    : reader.GetDateTime(reader.GetOrdinal("UltimaActividad")),
                EspacioMaximoMB = reader.GetInt16(reader.GetOrdinal("EspacioMaximoMB")),
                EspacioUtilizadoMB = reader.GetDecimal(reader.GetOrdinal("EspacioUtilizadoMB")),
            });
        }
        return resultado;
    }

    public async Task<CredencialDto?> ObtenerCredencialesAsync(long baseDeDatosId, long usuarioIdSolicitante, CancellationToken ct = default)
    {
        await using var conn = await _factory.AbrirAsync(ct);
        await using var cmd = new SqlCommand("dbo.sp_ObtenerCredencialesBaseDatos", conn)
        {
            CommandType = CommandType.StoredProcedure,
        };
        cmd.Parameters.Add("@BaseDeDatosId", SqlDbType.Int).Value = (int)baseDeDatosId;
        cmd.Parameters.Add("@UsuarioIdSolicitante", SqlDbType.Int).Value = (int)usuarioIdSolicitante;

        try
        {
            await using var reader = await cmd.ExecuteReaderAsync(ct);
            if (!await reader.ReadAsync(ct))
                return null;

            return new CredencialDto
            {
                Id = reader.GetInt32(reader.GetOrdinal("Id")),
                NombreBD = reader.GetString(reader.GetOrdinal("NombreBD")),
                UsuarioBD = reader.GetString(reader.GetOrdinal("UsuarioBD")),
                Password = reader.GetString(reader.GetOrdinal("Password")),
                Host = reader.GetString(reader.GetOrdinal("Host")),
                Puerto = reader.GetInt32(reader.GetOrdinal("Puerto")),
                Motor = reader.GetString(reader.GetOrdinal("Motor")),
                Estado = reader.GetString(reader.GetOrdinal("Estado")),
                FechaCreacion = reader.GetDateTime(reader.GetOrdinal("FechaCreacion")),
                UltimaActividad = reader.IsDBNull(reader.GetOrdinal("UltimaActividad"))
                    ? null
                    : reader.GetDateTime(reader.GetOrdinal("UltimaActividad")),
                EspacioMaximoMB = reader.GetInt16(reader.GetOrdinal("EspacioMaximoMB")),
                EspacioUtilizadoMB = reader.GetDecimal(reader.GetOrdinal("EspacioUtilizadoMB")),
            };
        }
        catch (SqlException ex) when (ex.Number is 50011 or 50012)
        {
            // Control 3.1 (BOLA): no existe (50011) o no es el dueño (50012). Se traducen
            // ambas al mismo resultado (null → 404) para no confirmar existencia a un no-dueño.
            return null;
        }
        catch (SqlException ex) when (ex.Number >= 50000)
        {
            throw new SpBusinessException(ex.Number, ex.Message);
        }
    }
}
