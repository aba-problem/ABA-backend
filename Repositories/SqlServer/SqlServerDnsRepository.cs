using System.Data;
using abaproblem.Contracts;
using abaproblem.Repositories.Interfaces;
using Microsoft.Data.SqlClient;

namespace abaproblem.Repositories.SqlServer;

/// <summary>
/// Entregable 3 — Implementación SQL Server del módulo DNS (ABA_Control). SOLO invoca SPs.
/// </summary>
public sealed class SqlServerDnsRepository : IDnsRepository
{
    private readonly ISqlConnectionFactory _factory;

    public SqlServerDnsRepository(ISqlConnectionFactory factory) => _factory = factory;

    public async Task<DnsRegistroReservaDto> ValidarYCrearAsync(long usuarioId, string subdominio, string tipoRegistro, string valor, string? ipOrigen, CancellationToken ct = default)
    {
        await using var conn = await _factory.AbrirAsync(ct);
        await using var cmd = new SqlCommand("dbo.sp_ValidarYCrearRegistroDns", conn) { CommandType = CommandType.StoredProcedure };
        cmd.Parameters.Add("@UsuarioId", SqlDbType.Int).Value = (int)usuarioId;
        cmd.Parameters.Add("@Subdominio", SqlDbType.VarChar, 40).Value = subdominio;
        cmd.Parameters.Add("@TipoRegistro", SqlDbType.VarChar, 10).Value = tipoRegistro;
        cmd.Parameters.Add("@Valor", SqlDbType.VarChar, 255).Value = valor;
        cmd.Parameters.Add("@IpOrigen", SqlDbType.VarChar, 45).Value = (object?)ipOrigen ?? DBNull.Value;

        try
        {
            await using var reader = await cmd.ExecuteReaderAsync(ct);
            if (!await reader.ReadAsync(ct))
                throw new InvalidOperationException("sp_ValidarYCrearRegistroDns no devolvió filas.");

            return LeerReserva(reader);
        }
        catch (SqlException ex) when (ex.Number >= 50000)
        {
            throw new SpBusinessException(ex.Number, ex.Message);
        }
    }

    public async Task ConfirmarAsync(int registroId, bool exitoso, string? ipOrigen, CancellationToken ct = default)
    {
        await using var conn = await _factory.AbrirAsync(ct);
        await using var cmd = new SqlCommand("dbo.sp_ConfirmarRegistroDns", conn) { CommandType = CommandType.StoredProcedure };
        cmd.Parameters.Add("@RegistroId", SqlDbType.Int).Value = registroId;
        cmd.Parameters.Add("@Exitoso", SqlDbType.Bit).Value = exitoso;
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

    public async Task<IReadOnlyList<DnsRegistroDto>> ListarMisRegistrosAsync(long usuarioId, CancellationToken ct = default)
    {
        await using var conn = await _factory.AbrirAsync(ct);
        await using var cmd = new SqlCommand("dbo.sp_ListarMisRegistrosDns", conn) { CommandType = CommandType.StoredProcedure };
        cmd.Parameters.Add("@UsuarioId", SqlDbType.Int).Value = (int)usuarioId;

        var resultado = new List<DnsRegistroDto>();
        await using var reader = await cmd.ExecuteReaderAsync(ct);
        while (await reader.ReadAsync(ct))
            resultado.Add(LeerRegistro(reader, incluyeUsuario: false));
        return resultado;
    }

    public async Task<DnsRegistroReservaDto?> EliminarAsync(long usuarioId, int registroId, string? ipOrigen, CancellationToken ct = default)
    {
        await using var conn = await _factory.AbrirAsync(ct);
        await using var cmd = new SqlCommand("dbo.sp_EliminarRegistroDns", conn) { CommandType = CommandType.StoredProcedure };
        cmd.Parameters.Add("@UsuarioId", SqlDbType.Int).Value = (int)usuarioId;
        cmd.Parameters.Add("@RegistroId", SqlDbType.Int).Value = registroId;
        cmd.Parameters.Add("@IpOrigen", SqlDbType.VarChar, 45).Value = (object?)ipOrigen ?? DBNull.Value;

        try
        {
            await using var reader = await cmd.ExecuteReaderAsync(ct);
            if (!await reader.ReadAsync(ct))
                return null;
            return LeerReserva(reader);
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

    public async Task<IReadOnlyList<DnsRegistroDto>> ListarTodosAdminAsync(long usuarioIdSolicitante, CancellationToken ct = default)
    {
        await using var conn = await _factory.AbrirAsync(ct);
        await using var cmd = new SqlCommand("dbo.sp_ListarTodosRegistrosDns", conn) { CommandType = CommandType.StoredProcedure };
        cmd.Parameters.Add("@UsuarioIdSolicitante", SqlDbType.Int).Value = (int)usuarioIdSolicitante;

        try
        {
            var resultado = new List<DnsRegistroDto>();
            await using var reader = await cmd.ExecuteReaderAsync(ct);
            while (await reader.ReadAsync(ct))
                resultado.Add(LeerRegistro(reader, incluyeUsuario: true));
            return resultado;
        }
        catch (SqlException ex) when (ex.Number >= 50000)
        {
            // Incluye 50045 (no autorizado) — defensa en profundidad si [Authorize(Roles=
            // "Admin")] llegara a fallar por un bug de mapeo de roles del JWT.
            throw new SpBusinessException(ex.Number, ex.Message);
        }
    }

    public async Task<DnsRegistroReservaDto?> EliminarAdminAsync(long usuarioIdSolicitante, int registroId, string? ipOrigen, CancellationToken ct = default)
    {
        await using var conn = await _factory.AbrirAsync(ct);
        await using var cmd = new SqlCommand("dbo.sp_EliminarRegistroDnsAdmin", conn) { CommandType = CommandType.StoredProcedure };
        cmd.Parameters.Add("@UsuarioIdSolicitante", SqlDbType.Int).Value = (int)usuarioIdSolicitante;
        cmd.Parameters.Add("@RegistroId", SqlDbType.Int).Value = registroId;
        cmd.Parameters.Add("@IpOrigen", SqlDbType.VarChar, 45).Value = (object?)ipOrigen ?? DBNull.Value;

        try
        {
            await using var reader = await cmd.ExecuteReaderAsync(ct);
            if (!await reader.ReadAsync(ct))
                return null;
            return LeerReserva(reader);
        }
        catch (SqlException ex) when (ex.Number is 50011)
        {
            return null;
        }
        catch (SqlException ex) when (ex.Number >= 50000)
        {
            throw new SpBusinessException(ex.Number, ex.Message);
        }
    }

    private static DnsRegistroReservaDto LeerReserva(SqlDataReader reader) => new()
    {
        Id = reader.GetOrdinalSafe("Id") is int idOrd ? reader.GetInt32(idOrd) : 0,
        Subdominio = reader.GetString(reader.GetOrdinal("Subdominio")),
        TipoRegistro = reader.GetString(reader.GetOrdinal("TipoRegistro")),
        Valor = reader.GetString(reader.GetOrdinal("Valor")),
    };

    private static DnsRegistroDto LeerRegistro(SqlDataReader reader, bool incluyeUsuario) => new()
    {
        Id = reader.GetInt32(reader.GetOrdinal("Id")),
        UsuarioId = incluyeUsuario ? reader.GetInt32(reader.GetOrdinal("UsuarioId")) : null,
        UsuarioCorreo = incluyeUsuario ? reader.GetString(reader.GetOrdinal("UsuarioCorreo")) : null,
        Subdominio = reader.GetString(reader.GetOrdinal("Subdominio")),
        TipoRegistro = reader.GetString(reader.GetOrdinal("TipoRegistro")),
        Valor = reader.GetString(reader.GetOrdinal("Valor")),
        Estado = reader.GetString(reader.GetOrdinal("Estado")),
        FechaCreacion = reader.GetDateTime(reader.GetOrdinal("FechaCreacion")),
    };
}

file static class SqlDataReaderExtensions
{
    /// <summary>sp_EliminarRegistroDns/sp_EliminarRegistroDnsAdmin no devuelven "Id" (ya lo
    /// tiene el llamador); evita un GetOrdinal que lanzaría IndexOutOfRangeException.</summary>
    public static int? GetOrdinalSafe(this SqlDataReader reader, string columnName)
    {
        for (var i = 0; i < reader.FieldCount; i++)
            if (string.Equals(reader.GetName(i), columnName, StringComparison.OrdinalIgnoreCase))
                return i;
        return null;
    }
}
