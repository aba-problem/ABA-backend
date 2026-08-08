namespace abaproblem.Contracts;

/// <summary>
/// Módulo 8 — Espejo del SELECT final de sp_RotarPasswordBaseDatosSocio.
/// Mismo criterio que ProvisioningResultDto: la contraseña se entrega UNA SOLA VEZ.
/// </summary>
public sealed record ResetCredencialesResultDto
{
    public required long BaseDeDatosId { get; init; }
    public required string NombreBD { get; init; }
    public required string UsuarioBD { get; init; }
    public required string Host { get; init; }
    public required int Puerto { get; init; }

    /// <summary>Se entrega una única vez. El backend NO la guarda ni la loguea (control 2.1).</summary>
    public required string PasswordNueva { get; init; }
}
