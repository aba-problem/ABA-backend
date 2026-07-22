namespace abaproblem.Contracts;

/// <summary>
/// Módulo 2 — Espejo del SELECT final de sp_AprovisionarBaseDatos.
/// Control 2.1: la contraseña se muestra UNA SOLA VEZ aquí y nunca se persiste en
/// texto plano fuera de la transacción de creación; en BaseDeDatos.PasswordCifrado
/// vive cifrada con SymKey_ABA_Credenciales (AES-256, gestionada dentro de SQL Server).
/// </summary>
public sealed record ProvisioningResultDto
{
    public required long BaseDeDatosId { get; init; }
    public required string NombreBD { get; init; }
    public required string UsuarioBD { get; init; }
    public required string Host { get; init; }
    public required int Puerto { get; init; }
    public required string Motor { get; init; }

    /// <summary>Se entrega una única vez. El backend NO la guarda ni la loguea (control 2.1).</summary>
    public required string PasswordTemporal { get; init; }
}
