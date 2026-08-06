using System.Data;
using abaproblem.Contracts;
using abaproblem.Repositories.Interfaces;
using Microsoft.Data.SqlClient;

namespace abaproblem.Repositories.SqlServer;

/// <summary>
/// Entregable 3 — Implementación SQL Server del módulo de API Keys (ABA_Control).
/// SOLO invoca SPs. Ninguna concatenación de SQL, ningún CommandType.Text.
/// </summary>
public sealed class SqlServerApiKeyRepository : IApiKeyRepository
{
    private readonly ISqlConnectionFactory _factory;

    public SqlServerApiKeyRepository(ISqlConnectionFactory factory) => _factory = factory;

    public async Task<ApiKeyCreadaDto> CrearAsync(long usuarioId, string? ipOrigen, CancellationToken ct = default)
    {
        await using var conn = await _factory.AbrirAsync(ct);
        await using var cmd = new SqlCommand("dbo.sp_CrearApiKey", conn) { CommandType = CommandType.StoredProcedure };
        cmd.Parameters.Add("@UsuarioId", SqlDbType.Int).Value = (int)usuarioId;
        cmd.Parameters.Add("@IpOrigen", SqlDbType.VarChar, 45).Value = (object?)ipOrigen ?? DBNull.Value;

        try
        {
            await using var reader = await cmd.ExecuteReaderAsync(ct);
            if (!await reader.ReadAsync(ct))
                throw new InvalidOperationException("sp_CrearApiKey no devolvió filas.");

            return new ApiKeyCreadaDto
            {
                Id = reader.GetInt32(reader.GetOrdinal("Id")),
                Prefijo = reader.GetString(reader.GetOrdinal("Prefijo")),
                KeyCompleta = reader.GetString(reader.GetOrdinal("KeyCompleta")),
                FechaCreacion = reader.GetDateTime(reader.GetOrdinal("FechaCreacion")),
            };
        }
        catch (SqlException ex) when (ex.Number >= 50000)
        {
            throw new SpBusinessException(ex.Number, ex.Message);
        }
    }

    public async Task<IReadOnlyList<ApiKeyDto>> ListarAsync(long usuarioId, CancellationToken ct = default)
    {
        await using var conn = await _factory.AbrirAsync(ct);
        await using var cmd = new SqlCommand("dbo.sp_ListarApiKeys", conn) { CommandType = CommandType.StoredProcedure };
        cmd.Parameters.Add("@UsuarioId", SqlDbType.Int).Value = (int)usuarioId;

        var resultado = new List<ApiKeyDto>();
        await using var reader = await cmd.ExecuteReaderAsync(ct);
        while (await reader.ReadAsync(ct))
        {
            resultado.Add(new ApiKeyDto
            {
                Id = reader.GetInt32(reader.GetOrdinal("Id")),
                Prefijo = reader.GetString(reader.GetOrdinal("Prefijo")),
                Activa = reader.GetBoolean(reader.GetOrdinal("Activa")),
                FechaCreacion = reader.GetDateTime(reader.GetOrdinal("FechaCreacion")),
                FechaRevocacion = reader.IsDBNull(reader.GetOrdinal("FechaRevocacion"))
                    ? null : reader.GetDateTime(reader.GetOrdinal("FechaRevocacion")),
                UltimoUso = reader.IsDBNull(reader.GetOrdinal("UltimoUso"))
                    ? null : reader.GetDateTime(reader.GetOrdinal("UltimoUso")),
            });
        }
        return resultado;
    }

    public async Task<bool?> RevocarAsync(long usuarioId, int apiKeyId, string? ipOrigen, CancellationToken ct = default)
    {
        await using var conn = await _factory.AbrirAsync(ct);
        await using var cmd = new SqlCommand("dbo.sp_RevocarApiKey", conn) { CommandType = CommandType.StoredProcedure };
        cmd.Parameters.Add("@UsuarioId", SqlDbType.Int).Value = (int)usuarioId;
        cmd.Parameters.Add("@ApiKeyId", SqlDbType.Int).Value = apiKeyId;
        cmd.Parameters.Add("@IpOrigen", SqlDbType.VarChar, 45).Value = (object?)ipOrigen ?? DBNull.Value;

        try
        {
            await cmd.ExecuteNonQueryAsync(ct);
            return true;
        }
        catch (SqlException ex) when (ex.Number is 50011 or 50012)
        {
            // Control 3.1 (BOLA): no existe o no es el dueño — mismo resultado (null → 404).
            return null;
        }
        catch (SqlException ex) when (ex.Number >= 50000)
        {
            throw new SpBusinessException(ex.Number, ex.Message);
        }
    }

    public async Task<ApiKeyCandidataDto?> ObtenerPorPrefijoAsync(string prefijo, CancellationToken ct = default)
    {
        await using var conn = await _factory.AbrirAsync(ct);
        await using var cmd = new SqlCommand("dbo.sp_ObtenerApiKeyPorPrefijo", conn) { CommandType = CommandType.StoredProcedure };
        cmd.Parameters.Add("@Prefijo", SqlDbType.Char, 8).Value = prefijo;

        await using var reader = await cmd.ExecuteReaderAsync(ct);
        if (!await reader.ReadAsync(ct))
            return null;

        return new ApiKeyCandidataDto
        {
            Id = reader.GetInt32(reader.GetOrdinal("Id")),
            UsuarioId = reader.GetInt32(reader.GetOrdinal("UsuarioId")),
            KeyHash = reader.GetFieldValue<byte[]>(reader.GetOrdinal("KeyHash")),
            Activa = reader.GetBoolean(reader.GetOrdinal("Activa")),
        };
    }

    public async Task RegistrarUsoAsync(int apiKeyId, string endpoint, int? tokensEstimados, CancellationToken ct = default)
    {
        await using var conn = await _factory.AbrirAsync(ct);
        await using var cmd = new SqlCommand("dbo.sp_RegistrarUsoApiKey", conn) { CommandType = CommandType.StoredProcedure };
        cmd.Parameters.Add("@ApiKeyId", SqlDbType.Int).Value = apiKeyId;
        cmd.Parameters.Add("@Endpoint", SqlDbType.VarChar, 200).Value = endpoint;
        cmd.Parameters.Add("@TokensEstimados", SqlDbType.Int).Value = (object?)tokensEstimados ?? DBNull.Value;

        await cmd.ExecuteNonQueryAsync(ct);
    }

    public async Task<IReadOnlyList<ApiKeyConsumoDiaDto>> ObtenerConsumoAsync(long usuarioId, int apiKeyId, CancellationToken ct = default)
    {
        await using var conn = await _factory.AbrirAsync(ct);
        await using var cmd = new SqlCommand("dbo.sp_ObtenerConsumoApiKey", conn) { CommandType = CommandType.StoredProcedure };
        cmd.Parameters.Add("@UsuarioId", SqlDbType.Int).Value = (int)usuarioId;
        cmd.Parameters.Add("@ApiKeyId", SqlDbType.Int).Value = apiKeyId;

        try
        {
            var resultado = new List<ApiKeyConsumoDiaDto>();
            await using var reader = await cmd.ExecuteReaderAsync(ct);
            while (await reader.ReadAsync(ct))
            {
                resultado.Add(new ApiKeyConsumoDiaDto
                {
                    Dia = reader.GetDateTime(reader.GetOrdinal("Dia")),
                    Llamadas = reader.GetInt32(reader.GetOrdinal("Llamadas")),
                    TokensTotales = reader.GetInt32(reader.GetOrdinal("TokensTotales")),
                });
            }
            return resultado;
        }
        catch (SqlException ex) when (ex.Number >= 50000)
        {
            throw new SpBusinessException(ex.Number, ex.Message);
        }
    }
}
