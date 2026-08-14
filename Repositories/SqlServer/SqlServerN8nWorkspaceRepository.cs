using System.Data;
using abaproblem.Contracts;
using abaproblem.Repositories.Interfaces;
using Microsoft.Data.SqlClient;

namespace abaproblem.Repositories.SqlServer;

/// <summary>
/// Entregable 3 — Implementación SQL Server del módulo N8N (ABA_Control). SOLO invoca SPs.
/// </summary>
public sealed class SqlServerN8nWorkspaceRepository : IN8nWorkspaceRepository
{
    private readonly ISqlConnectionFactory _factory;

    public SqlServerN8nWorkspaceRepository(ISqlConnectionFactory factory) => _factory = factory;

    public async Task<N8nWorkspaceCreadoDto> RegistrarExternoAsync(
        long usuarioId, string accountIdExterno, string email, string credencialUrl, string? ipOrigen, CancellationToken ct = default)
    {
        await using var conn = await _factory.AbrirAsync(ct);
        await using var cmd = new SqlCommand("dbo.sp_RegistrarWorkspaceN8NExterno", conn)
        {
            CommandType = CommandType.StoredProcedure,
        };
        // usuarioId SIEMPRE del claim JWT (control BOLA), nunca del body.
        cmd.Parameters.Add("@UsuarioId", SqlDbType.Int).Value = (int)usuarioId;
        cmd.Parameters.Add("@AccountIdExterno", SqlDbType.VarChar, 64).Value = accountIdExterno;
        cmd.Parameters.Add("@Email", SqlDbType.NVarChar, 255).Value = email;
        cmd.Parameters.Add("@CredencialUrl", SqlDbType.NVarChar, 1000).Value = credencialUrl;
        cmd.Parameters.Add("@IpOrigen", SqlDbType.VarChar, 45).Value = (object?)ipOrigen ?? DBNull.Value;

        try
        {
            await using var reader = await cmd.ExecuteReaderAsync(ct);
            if (!await reader.ReadAsync(ct))
                throw new InvalidOperationException("sp_RegistrarWorkspaceN8NExterno no devolvió filas.");

            return new N8nWorkspaceCreadoDto
            {
                Id = reader.GetInt32(reader.GetOrdinal("Id")),
                NombreWorkspace = reader.GetString(reader.GetOrdinal("NombreWorkspace")),
                CredencialUrl = reader.GetString(reader.GetOrdinal("CredencialUrl")),
                LimiteWorkflows = reader.GetInt32(reader.GetOrdinal("LimiteWorkflows")),
                LimiteEjecucionesMes = reader.GetInt32(reader.GetOrdinal("LimiteEjecucionesMes")),
            };
        }
        catch (SqlException ex) when (ex.Number >= 50000)
        {
            throw new SpBusinessException(ex.Number, ex.Message);
        }
    }

    public async Task<N8nWorkspaceDto?> ObtenerMiWorkspaceAsync(long usuarioId, CancellationToken ct = default)
    {
        await using var conn = await _factory.AbrirAsync(ct);
        await using var cmd = new SqlCommand("dbo.sp_ObtenerMiWorkspace", conn)
        {
            CommandType = CommandType.StoredProcedure,
        };
        cmd.Parameters.Add("@UsuarioId", SqlDbType.Int).Value = (int)usuarioId;

        await using var reader = await cmd.ExecuteReaderAsync(ct);
        if (!await reader.ReadAsync(ct))
            return null;

        return new N8nWorkspaceDto
        {
            Id = reader.GetInt32(reader.GetOrdinal("Id")),
            NombreWorkspace = reader.GetString(reader.GetOrdinal("NombreWorkspace")),
            LimiteWorkflows = reader.GetInt32(reader.GetOrdinal("LimiteWorkflows")),
            LimiteEjecucionesMes = reader.GetInt32(reader.GetOrdinal("LimiteEjecucionesMes")),
            Estado = reader.GetString(reader.GetOrdinal("Estado")),
            FechaCreacion = reader.GetDateTime(reader.GetOrdinal("FechaCreacion")),
        };
    }

    public async Task EliminarAsync(long usuarioId, string? ipOrigen, CancellationToken ct = default)
    {
        await using var conn = await _factory.AbrirAsync(ct);
        await using var cmd = new SqlCommand("dbo.sp_EliminarWorkspace", conn)
        {
            CommandType = CommandType.StoredProcedure,
        };
        cmd.Parameters.Add("@UsuarioId", SqlDbType.Int).Value = (int)usuarioId;
        cmd.Parameters.Add("@IpOrigen", SqlDbType.VarChar, 45).Value = (object?)ipOrigen ?? DBNull.Value;

        try
        {
            await cmd.ExecuteNonQueryAsync(ct);
        }
        catch (SqlException ex) when (ex.Number >= 50000)
        {
            throw new SpBusinessException(ex.Number, ex.Message);
        }
    }
}
