using System.Data;
using abaproblem.Contracts;
using abaproblem.Repositories.Interfaces;
using Microsoft.Data.SqlClient;

namespace abaproblem.Repositories.SqlServer;

/// <summary>
/// Módulo 1 — Implementación SQL Server del repositorio de usuarios (ABA_Control).
/// SOLO invoca el SP sp_CrearUsuario con parámetros tipados (previene inyección).
/// Ninguna concatenación de SQL, ningún CommandType.Text.
/// </summary>
public sealed class SqlServerUsuarioRepository : IUsuarioRepository
{
    private readonly ISqlConnectionFactory _factory;

    public SqlServerUsuarioRepository(ISqlConnectionFactory factory) => _factory = factory;

    public async Task<UsuarioDto> ObtenerOCrearAsync(ExternalLoginInfo info, string? ipOrigen, CancellationToken ct = default)
    {
        await using var conn = await _factory.AbrirAsync(ct);
        await using var cmd = new SqlCommand("dbo.sp_CrearUsuario", conn)
        {
            CommandType = CommandType.StoredProcedure,
        };

        // Parámetros tipados y con longitud fija → el motor no interpreta el valor como código.
        cmd.Parameters.Add("@Nombre", SqlDbType.NVarChar, 150).Value = info.Nombre;
        cmd.Parameters.Add("@Correo", SqlDbType.NVarChar, 255).Value = info.Correo;
        cmd.Parameters.Add("@AvatarUrl", SqlDbType.NVarChar, 500).Value = (object?)info.AvatarUrl ?? DBNull.Value;
        cmd.Parameters.Add("@Proveedor", SqlDbType.VarChar, 20).Value = info.Proveedor;
        cmd.Parameters.Add("@ProveedorUsuarioId", SqlDbType.VarChar, 100).Value = info.ProveedorUsuarioId;
        cmd.Parameters.Add("@IpOrigen", SqlDbType.VarChar, 45).Value = (object?)ipOrigen ?? DBNull.Value;

        try
        {
            await using var reader = await cmd.ExecuteReaderAsync(ct);
            if (!await reader.ReadAsync(ct))
                throw new InvalidOperationException("sp_CrearUsuario no devolvió filas.");

            return new UsuarioDto
            {
                UsuarioId = reader.GetInt32(reader.GetOrdinal("Id")),
                Nombre = reader.GetString(reader.GetOrdinal("Nombre")),
                Correo = reader.GetString(reader.GetOrdinal("Correo")),
                AvatarUrl = reader.IsDBNull(reader.GetOrdinal("AvatarUrl"))
                    ? null
                    : reader.GetString(reader.GetOrdinal("AvatarUrl")),
                Proveedor = reader.GetString(reader.GetOrdinal("Proveedor")),
                FechaCreacion = reader.GetDateTime(reader.GetOrdinal("FechaCreacion")),
                UltimoLogin = reader.IsDBNull(reader.GetOrdinal("UltimoLogin"))
                    ? null
                    : reader.GetDateTime(reader.GetOrdinal("UltimoLogin")),
            };
        }
        catch (SqlException ex) when (ex.Number >= 50000)
        {
            // Error estructurado del SP (THROW 5xxxx) → excepción de negocio, sin filtrar detalle interno.
            throw new SpBusinessException(ex.Number, ex.Message);
        }
    }
}
