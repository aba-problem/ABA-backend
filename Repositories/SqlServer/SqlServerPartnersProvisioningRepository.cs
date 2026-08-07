using System.Data;
using abaproblem.Contracts;
using abaproblem.Repositories.Interfaces;
using Microsoft.Data.SqlClient;

namespace abaproblem.Repositories.SqlServer;

/// <summary>
/// Módulo 8 — Implementación SQL Server del aprovisionamiento para células socias.
/// SOLO invoca SPs. El backend NO decide nombres, prefijos ni límites.
/// </summary>
public sealed class SqlServerPartnersProvisioningRepository : IPartnersProvisioningRepository
{
    private readonly ISqlConnectionFactory _factory;

    public SqlServerPartnersProvisioningRepository(ISqlConnectionFactory factory) => _factory = factory;

    public async Task<ProvisioningResultDto> AprovisionarAsync(int celulaSociaId, string nombreMotor, CancellationToken ct = default)
    {
        await using var conn = await _factory.AbrirAsync(ct);
        await using var cmd = new SqlCommand("dbo.sp_AprovisionarBaseDatosSocio", conn)
        {
            CommandType = CommandType.StoredProcedure,
            CommandTimeout = 30,
        };

        // celulaSociaId proviene SIEMPRE del claim resuelto por PartnerApiKeyAuthenticationHandler
        // (control BOLA), nunca del body de la request.
        cmd.Parameters.Add("@CelulaSociaId", SqlDbType.Int).Value = celulaSociaId;
        cmd.Parameters.Add("@NombreMotor", SqlDbType.VarChar, 30).Value = nombreMotor;

        try
        {
            await using var reader = await cmd.ExecuteReaderAsync(ct);
            if (!await reader.ReadAsync(ct))
                throw new InvalidOperationException("sp_AprovisionarBaseDatosSocio no devolvió filas.");

            return new ProvisioningResultDto
            {
                BaseDeDatosId = reader.GetInt32(reader.GetOrdinal("BaseDeDatosId")),
                NombreBD = reader.GetString(reader.GetOrdinal("NombreBD")),
                UsuarioBD = reader.GetString(reader.GetOrdinal("UsuarioBD")),
                Host = reader.GetString(reader.GetOrdinal("Host")),
                Puerto = reader.GetInt32(reader.GetOrdinal("Puerto")),
                Motor = reader.GetString(reader.GetOrdinal("Motor")),
                PasswordTemporal = reader.GetString(reader.GetOrdinal("PasswordTemporal")),
            };
        }
        catch (SqlException ex) when (ex.Number >= 50000)
        {
            throw new SpBusinessException(ex.Number, ex.Message);
        }
    }

    public async Task ConfirmarAsync(long baseDeDatosSocioId, bool exitoso, CancellationToken ct = default)
    {
        await using var conn = await _factory.AbrirAsync(ct);
        await using var cmd = new SqlCommand("dbo.sp_ConfirmarAprovisionamientoSocio", conn)
        {
            CommandType = CommandType.StoredProcedure,
        };
        cmd.Parameters.Add("@BaseDeDatosSocioId", SqlDbType.Int).Value = (int)baseDeDatosSocioId;
        cmd.Parameters.Add("@Exitoso", SqlDbType.Bit).Value = exitoso;

        try
        {
            await cmd.ExecuteNonQueryAsync(ct);
        }
        catch (SqlException ex) when (ex.Number >= 50000)
        {
            throw new SpBusinessException(ex.Number, ex.Message);
        }
    }

    public async Task<BaseDatosSocioDto?> ObtenerPorIdAsync(long baseDeDatosSocioId, int celulaSociaId, CancellationToken ct = default)
    {
        await using var conn = await _factory.AbrirAsync(ct);
        await using var cmd = new SqlCommand("dbo.sp_ObtenerBaseDatosSocioPorId", conn)
        {
            CommandType = CommandType.StoredProcedure,
        };
        cmd.Parameters.Add("@BaseDeDatosSocioId", SqlDbType.Int).Value = (int)baseDeDatosSocioId;
        cmd.Parameters.Add("@CelulaSociaId", SqlDbType.Int).Value = celulaSociaId;

        await using var reader = await cmd.ExecuteReaderAsync(ct);
        if (!await reader.ReadAsync(ct))
            return null; // no existe o no pertenece a esta célula (control BOLA)

        return new BaseDatosSocioDto
        {
            Id = reader.GetInt32(reader.GetOrdinal("Id")),
            NombreBD = reader.GetString(reader.GetOrdinal("NombreBD")),
            UsuarioBD = reader.GetString(reader.GetOrdinal("UsuarioBD")),
            Host = reader.GetString(reader.GetOrdinal("Host")),
            Puerto = reader.GetInt32(reader.GetOrdinal("Puerto")),
            Estado = reader.GetString(reader.GetOrdinal("Estado")),
            EspacioMaximoMB = reader.GetInt16(reader.GetOrdinal("EspacioMaximoMB")),
            EspacioUtilizadoMB = reader.GetDecimal(reader.GetOrdinal("EspacioUtilizadoMB")),
            FechaCreacion = reader.GetDateTime(reader.GetOrdinal("FechaCreacion")),
        };
    }

    public async Task DesactivarAsync(long baseDeDatosSocioId, int celulaSociaId, CancellationToken ct = default)
    {
        await using var conn = await _factory.AbrirAsync(ct);
        await using var cmd = new SqlCommand("dbo.sp_DesactivarBaseDatosSocio", conn)
        {
            CommandType = CommandType.StoredProcedure,
        };
        cmd.Parameters.Add("@BaseDeDatosSocioId", SqlDbType.Int).Value = (int)baseDeDatosSocioId;
        cmd.Parameters.Add("@CelulaSociaId", SqlDbType.Int).Value = celulaSociaId;

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
