using System.Data;
using abaproblem.Contracts;
using abaproblem.Repositories.Interfaces;
using Microsoft.Data.SqlClient;

namespace abaproblem.Repositories.SqlServer;

public sealed class SqlServerIpWhitelistRepository : IIpWhitelistRepository
{
    private readonly ISqlConnectionFactory _factory;

    public SqlServerIpWhitelistRepository(ISqlConnectionFactory factory) => _factory = factory;

    public async Task<IpRegistroDto> RegistrarAsync(long usuarioId, string direccionIp, string paisIso, CancellationToken ct = default)
    {
        await using var conn = await _factory.AbrirAsync(ct);
        await using var cmd = new SqlCommand("dbo.sp_RegistrarIpUsuario", conn)
        {
            CommandType = CommandType.StoredProcedure,
        };
        cmd.Parameters.Add("@UsuarioId", SqlDbType.Int).Value = (int)usuarioId;
        cmd.Parameters.Add("@DireccionIp", SqlDbType.VarChar, 45).Value = direccionIp;
        cmd.Parameters.Add("@PaisIso", SqlDbType.Char, 2).Value = paisIso;

        try
        {
            await using var reader = await cmd.ExecuteReaderAsync(ct);
            if (!await reader.ReadAsync(ct))
                throw new InvalidOperationException("sp_RegistrarIpUsuario no devolvió filas.");

            return new IpRegistroDto
            {
                Id = reader.GetInt32(reader.GetOrdinal("Id")),
                DireccionIp = reader.GetString(reader.GetOrdinal("DireccionIp")),
                PaisIso = reader.GetString(reader.GetOrdinal("PaisIso")),
                Activo = reader.GetBoolean(reader.GetOrdinal("Activo")),
                FechaVerificacion = reader.IsDBNull(reader.GetOrdinal("FechaVerificacion"))
                    ? null
                    : reader.GetDateTime(reader.GetOrdinal("FechaVerificacion")),
            };
        }
        catch (SqlException ex) when (ex.Number >= 50000)
        {
            // 50010: país fuera de América/Latam (ya auditado como IP_RECHAZADA dentro del SP).
            throw new SpBusinessException(ex.Number, ex.Message);
        }
    }

    public async Task<IReadOnlyList<string>> ListarUsuarioBDMySqlAsync(long usuarioId, CancellationToken ct = default)
    {
        await using var conn = await _factory.AbrirAsync(ct);
        await using var cmd = new SqlCommand("dbo.sp_ListarUsuarioBDMySql", conn)
        {
            CommandType = CommandType.StoredProcedure,
        };
        cmd.Parameters.Add("@UsuarioId", SqlDbType.Int).Value = (int)usuarioId;

        var resultado = new List<string>();
        await using var reader = await cmd.ExecuteReaderAsync(ct);
        while (await reader.ReadAsync(ct))
            resultado.Add(reader.GetString(0));
        return resultado;
    }

    public async Task<IReadOnlyList<string>> ListarIpsActivasAsync(long usuarioId, CancellationToken ct = default)
    {
        await using var conn = await _factory.AbrirAsync(ct);
        await using var cmd = new SqlCommand("dbo.sp_ListarIpsActivasUsuario", conn)
        {
            CommandType = CommandType.StoredProcedure,
        };
        cmd.Parameters.Add("@UsuarioId", SqlDbType.Int).Value = (int)usuarioId;

        var resultado = new List<string>();
        await using var reader = await cmd.ExecuteReaderAsync(ct);
        while (await reader.ReadAsync(ct))
            resultado.Add(reader.GetString(0));
        return resultado;
    }
}
