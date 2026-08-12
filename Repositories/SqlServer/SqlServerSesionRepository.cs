using System.Data;
using abaproblem.Contracts;
using abaproblem.Repositories.Interfaces;
using Microsoft.Data.SqlClient;

namespace abaproblem.Repositories.SqlServer;

public sealed class SqlServerSesionRepository : ISesionRepository
{
    private readonly ISqlConnectionFactory _factory;

    public SqlServerSesionRepository(ISqlConnectionFactory factory) => _factory = factory;

    public async Task<SesionesPaginadasDto> ListarAsync(long usuarioId, int pagina, int tamanoPagina, CancellationToken ct = default)
    {
        await using var conn = await _factory.AbrirAsync(ct);
        await using var cmd = new SqlCommand("dbo.sp_ListarSesionesUsuario", conn)
        {
            CommandType = CommandType.StoredProcedure,
        };
        cmd.Parameters.Add("@UsuarioId", SqlDbType.Int).Value = (int)usuarioId;
        cmd.Parameters.Add("@Pagina", SqlDbType.Int).Value = pagina;
        cmd.Parameters.Add("@TamanoPagina", SqlDbType.Int).Value = tamanoPagina;

        var registros = new List<SesionRegistroDto>();
        var total = 0;

        await using var reader = await cmd.ExecuteReaderAsync(ct);
        while (await reader.ReadAsync(ct))
        {
            registros.Add(new SesionRegistroDto
            {
                Id = reader.GetInt64(reader.GetOrdinal("Id")),
                Entidad = reader.GetString(reader.GetOrdinal("Entidad")),
                Accion = reader.GetString(reader.GetOrdinal("Accion")),
                IpOrigen = reader.IsDBNull(reader.GetOrdinal("IpOrigen")) ? null : reader.GetString(reader.GetOrdinal("IpOrigen")),
                FechaEvento = reader.GetDateTime(reader.GetOrdinal("FechaEvento")),
                Detalle = reader.IsDBNull(reader.GetOrdinal("Detalle")) ? null : reader.GetString(reader.GetOrdinal("Detalle")),
            });
            total = reader.GetInt32(reader.GetOrdinal("TotalRegistros"));
        }

        return new SesionesPaginadasDto
        {
            Registros = registros,
            Total = total,
            Pagina = pagina,
            TamanoPagina = tamanoPagina,
        };
    }

    public async Task<bool> RevocarIpAsync(long usuarioId, string direccionIp, string? ipOrigenSolicitud, CancellationToken ct = default)
    {
        await using var conn = await _factory.AbrirAsync(ct);
        await using var cmd = new SqlCommand("dbo.sp_RevocarIpUsuario", conn)
        {
            CommandType = CommandType.StoredProcedure,
        };
        cmd.Parameters.Add("@UsuarioId", SqlDbType.Int).Value = (int)usuarioId;
        cmd.Parameters.Add("@DireccionIp", SqlDbType.VarChar, 45).Value = direccionIp;
        cmd.Parameters.Add("@IpOrigenSolicitud", SqlDbType.VarChar, 45).Value = (object?)ipOrigenSolicitud ?? DBNull.Value;

        try
        {
            await cmd.ExecuteNonQueryAsync(ct);
            return true;
        }
        catch (SqlException ex) when (ex.Number is 50011)
        {
            return false;
        }
        catch (SqlException ex) when (ex.Number >= 50000)
        {
            throw new SpBusinessException(ex.Number, ex.Message);
        }
    }
}
