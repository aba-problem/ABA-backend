using System.Data;
using abaproblem.Contracts;
using abaproblem.Repositories.Interfaces;
using Microsoft.Data.SqlClient;

namespace abaproblem.Repositories.SqlServer;

public sealed class SqlServerSesionRepository : ISesionRepository
{
    private readonly ISqlConnectionFactory _factory;

    public SqlServerSesionRepository(ISqlConnectionFactory factory) => _factory = factory;

    public async Task<IReadOnlyList<SesionRegistroDto>> ListarAsync(long usuarioId, CancellationToken ct = default)
    {
        await using var conn = await _factory.AbrirAsync(ct);
        await using var cmd = new SqlCommand("dbo.sp_ListarSesionesUsuario", conn)
        {
            CommandType = CommandType.StoredProcedure,
        };
        cmd.Parameters.Add("@UsuarioId", SqlDbType.Int).Value = (int)usuarioId;

        var resultado = new List<SesionRegistroDto>();
        await using var reader = await cmd.ExecuteReaderAsync(ct);
        while (await reader.ReadAsync(ct))
        {
            resultado.Add(new SesionRegistroDto
            {
                Id = reader.GetInt64(reader.GetOrdinal("Id")),
                Entidad = reader.GetString(reader.GetOrdinal("Entidad")),
                Accion = reader.GetString(reader.GetOrdinal("Accion")),
                IpOrigen = reader.IsDBNull(reader.GetOrdinal("IpOrigen")) ? null : reader.GetString(reader.GetOrdinal("IpOrigen")),
                FechaEvento = reader.GetDateTime(reader.GetOrdinal("FechaEvento")),
                Detalle = reader.IsDBNull(reader.GetOrdinal("Detalle")) ? null : reader.GetString(reader.GetOrdinal("Detalle")),
            });
        }
        return resultado;
    }
}
