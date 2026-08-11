using System.Data;
using abaproblem.Contracts;
using abaproblem.Repositories.Interfaces;
using Microsoft.Data.SqlClient;

namespace abaproblem.Repositories.SqlServer;

/// <summary>Solo invoca SPs — ver ICelulasSociasAdminRepository.</summary>
public sealed class SqlServerCelulasSociasAdminRepository : ICelulasSociasAdminRepository
{
    private readonly ISqlConnectionFactory _factory;

    public SqlServerCelulasSociasAdminRepository(ISqlConnectionFactory factory) => _factory = factory;

    public async Task<CelulaSociaCreadaDto> AltaAsync(long usuarioIdSolicitante, string nombreCelula, string prefijo, CancellationToken ct = default)
    {
        await using var conn = await _factory.AbrirAsync(ct);
        await using var cmd = new SqlCommand("dbo.sp_AltaCelulaSociaAutoKey", conn)
        {
            CommandType = CommandType.StoredProcedure,
        };
        cmd.Parameters.Add("@UsuarioIdSolicitante", SqlDbType.Int).Value = (int)usuarioIdSolicitante;
        cmd.Parameters.Add("@NombreCelula", SqlDbType.VarChar, 50).Value = nombreCelula;
        cmd.Parameters.Add("@Prefijo", SqlDbType.VarChar, 20).Value = prefijo;

        try
        {
            await using var reader = await cmd.ExecuteReaderAsync(ct);
            if (!await reader.ReadAsync(ct))
                throw new InvalidOperationException("sp_AltaCelulaSociaAutoKey no devolvió filas.");

            return MapearCreada(reader);
        }
        catch (SqlException ex) when (ex.Number >= 50000)
        {
            throw new SpBusinessException(ex.Number, ex.Message);
        }
    }

    public async Task<IReadOnlyList<CelulaSociaResumenDto>> ListarAsync(long usuarioIdSolicitante, CancellationToken ct = default)
    {
        await using var conn = await _factory.AbrirAsync(ct);
        await using var cmd = new SqlCommand("dbo.sp_ListarCelulasSocias", conn)
        {
            CommandType = CommandType.StoredProcedure,
        };
        cmd.Parameters.Add("@UsuarioIdSolicitante", SqlDbType.Int).Value = (int)usuarioIdSolicitante;

        try
        {
            var resultado = new List<CelulaSociaResumenDto>();
            await using var reader = await cmd.ExecuteReaderAsync(ct);
            while (await reader.ReadAsync(ct))
                resultado.Add(MapearBase(reader));
            return resultado;
        }
        catch (SqlException ex) when (ex.Number >= 50000)
        {
            throw new SpBusinessException(ex.Number, ex.Message);
        }
    }

    public async Task<CelulaSociaResumenDto> CambiarEstadoAsync(long usuarioIdSolicitante, int celulaSociaId, bool activo, CancellationToken ct = default)
    {
        await using var conn = await _factory.AbrirAsync(ct);
        await using var cmd = new SqlCommand("dbo.sp_CambiarEstadoCelulaSocia", conn)
        {
            CommandType = CommandType.StoredProcedure,
        };
        cmd.Parameters.Add("@UsuarioIdSolicitante", SqlDbType.Int).Value = (int)usuarioIdSolicitante;
        cmd.Parameters.Add("@CelulaSociaId", SqlDbType.Int).Value = celulaSociaId;
        cmd.Parameters.Add("@Activo", SqlDbType.Bit).Value = activo;

        try
        {
            await using var reader = await cmd.ExecuteReaderAsync(ct);
            if (!await reader.ReadAsync(ct))
                throw new InvalidOperationException("sp_CambiarEstadoCelulaSocia no devolvió filas.");

            return MapearBase(reader);
        }
        catch (SqlException ex) when (ex.Number >= 50000)
        {
            throw new SpBusinessException(ex.Number, ex.Message);
        }
    }

    public async Task<CelulaSociaCreadaDto> RotarApiKeyAsync(long usuarioIdSolicitante, int celulaSociaId, CancellationToken ct = default)
    {
        await using var conn = await _factory.AbrirAsync(ct);
        await using var cmd = new SqlCommand("dbo.sp_RotarApiKeyCelulaSocia", conn)
        {
            CommandType = CommandType.StoredProcedure,
        };
        cmd.Parameters.Add("@UsuarioIdSolicitante", SqlDbType.Int).Value = (int)usuarioIdSolicitante;
        cmd.Parameters.Add("@CelulaSociaId", SqlDbType.Int).Value = celulaSociaId;

        try
        {
            await using var reader = await cmd.ExecuteReaderAsync(ct);
            if (!await reader.ReadAsync(ct))
                throw new InvalidOperationException("sp_RotarApiKeyCelulaSocia no devolvió filas.");

            return MapearCreada(reader);
        }
        catch (SqlException ex) when (ex.Number >= 50000)
        {
            throw new SpBusinessException(ex.Number, ex.Message);
        }
    }

    private static CelulaSociaResumenDto MapearBase(SqlDataReader reader) => new()
    {
        Id = reader.GetInt32(reader.GetOrdinal("Id")),
        NombreCelula = reader.GetString(reader.GetOrdinal("NombreCelula")),
        Prefijo = reader.GetString(reader.GetOrdinal("Prefijo")),
        Activo = reader.GetBoolean(reader.GetOrdinal("Activo")),
        FechaCreacion = reader.GetDateTime(reader.GetOrdinal("FechaCreacion")),
    };

    private static CelulaSociaCreadaDto MapearCreada(SqlDataReader reader) => new()
    {
        Id = reader.GetInt32(reader.GetOrdinal("Id")),
        NombreCelula = reader.GetString(reader.GetOrdinal("NombreCelula")),
        Prefijo = reader.GetString(reader.GetOrdinal("Prefijo")),
        Activo = reader.GetBoolean(reader.GetOrdinal("Activo")),
        FechaCreacion = reader.GetDateTime(reader.GetOrdinal("FechaCreacion")),
        ApiKey = reader.GetString(reader.GetOrdinal("ApiKeyPlano")),
    };
}
