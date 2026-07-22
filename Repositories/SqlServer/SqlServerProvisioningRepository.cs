using System.Data;
using abaproblem.Contracts;
using abaproblem.Repositories.Interfaces;
using Microsoft.Data.SqlClient;

namespace abaproblem.Repositories.SqlServer;

/// <summary>
/// Módulo 2 — Implementación SQL Server del aprovisionamiento (ABA_Control).
/// SOLO invoca SPs. El backend NO decide nombres, tamaños ni límites (control 2.1/2.2).
/// </summary>
public sealed class SqlServerProvisioningRepository : IProvisioningRepository
{
    private readonly ISqlConnectionFactory _factory;

    public SqlServerProvisioningRepository(ISqlConnectionFactory factory) => _factory = factory;

    public async Task<ProvisioningResultDto> AprovisionarAsync(long usuarioId, string nombreMotor, string? ipOrigen, CancellationToken ct = default)
    {
        await using var conn = await _factory.AbrirAsync(ct);
        await using var cmd = new SqlCommand("dbo.sp_AprovisionarBaseDatos", conn)
        {
            CommandType = CommandType.StoredProcedure,
            CommandTimeout = 30,
        };

        // El usuarioId proviene SIEMPRE del claim JWT (control BOLA), nunca del body.
        cmd.Parameters.Add("@UsuarioId", SqlDbType.Int).Value = (int)usuarioId;
        cmd.Parameters.Add("@NombreMotor", SqlDbType.VarChar, 30).Value = nombreMotor;
        cmd.Parameters.Add("@IpOrigen", SqlDbType.VarChar, 45).Value = (object?)ipOrigen ?? DBNull.Value;

        try
        {
            await using var reader = await cmd.ExecuteReaderAsync(ct);
            if (!await reader.ReadAsync(ct))
                throw new InvalidOperationException("sp_AprovisionarBaseDatos no devolvió filas.");

            return new ProvisioningResultDto
            {
                BaseDeDatosId = reader.GetInt32(reader.GetOrdinal("BaseDeDatosId")),
                NombreBD = reader.GetString(reader.GetOrdinal("NombreBD")),
                UsuarioBD = reader.GetString(reader.GetOrdinal("UsuarioBD")),
                Host = reader.GetString(reader.GetOrdinal("Host")),
                Puerto = reader.GetInt32(reader.GetOrdinal("Puerto")),
                Motor = reader.GetString(reader.GetOrdinal("Motor")),
                // La contraseña la genera el SP (CRYPT_GEN_RANDOM) y se devuelve una única vez.
                PasswordTemporal = reader.GetString(reader.GetOrdinal("PasswordTemporal")),
            };
        }
        catch (SqlException ex) when (ex.Number >= 50000)
        {
            // Límite de bases excedido, motor inexistente, etc. → THROW 5xxxx estructurado.
            throw new SpBusinessException(ex.Number, ex.Message);
        }
    }

    public async Task ConfirmarAsync(long baseDeDatosId, bool exitoso, string? ipOrigen, CancellationToken ct = default)
    {
        await using var conn = await _factory.AbrirAsync(ct);
        await using var cmd = new SqlCommand("dbo.sp_ConfirmarAprovisionamiento", conn)
        {
            CommandType = CommandType.StoredProcedure,
        };
        cmd.Parameters.Add("@BaseDeDatosId", SqlDbType.Int).Value = (int)baseDeDatosId;
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

    public async Task ActualizarEspacioUsadoAsync(long baseDeDatosId, decimal espacioUtilizadoMB, CancellationToken ct = default)
    {
        await using var conn = await _factory.AbrirAsync(ct);
        await using var cmd = new SqlCommand("dbo.sp_ActualizarEspacioUsado", conn)
        {
            CommandType = CommandType.StoredProcedure,
        };
        cmd.Parameters.Add("@BaseDeDatosId", SqlDbType.Int).Value = (int)baseDeDatosId;
        cmd.Parameters.Add("@EspacioUtilizadoMB", SqlDbType.Decimal, 10).Value = espacioUtilizadoMB;

        await cmd.ExecuteNonQueryAsync(ct);
    }

    public async Task<IReadOnlyList<BaseParaOperarDto>> ListarActivasMySqlAsync(CancellationToken ct = default)
    {
        await using var conn = await _factory.AbrirAsync(ct);
        await using var cmd = new SqlCommand("dbo.sp_ListarBasesActivasMySql", conn)
        {
            CommandType = CommandType.StoredProcedure,
        };

        var resultado = new List<BaseParaOperarDto>();
        await using var reader = await cmd.ExecuteReaderAsync(ct);
        while (await reader.ReadAsync(ct))
        {
            resultado.Add(new BaseParaOperarDto
            {
                Id = reader.GetInt32(reader.GetOrdinal("Id")),
                UsuarioId = reader.GetInt32(reader.GetOrdinal("UsuarioId")),
                NombreBD = reader.GetString(reader.GetOrdinal("NombreBD")),
                UsuarioBD = reader.GetString(reader.GetOrdinal("UsuarioBD")),
                EspacioMaximoMB = reader.GetInt16(reader.GetOrdinal("EspacioMaximoMB")),
                EspacioUtilizadoMB = reader.GetDecimal(reader.GetOrdinal("EspacioUtilizadoMB")),
                Estado = reader.GetString(reader.GetOrdinal("Estado")),
            });
        }
        return resultado;
    }
}
