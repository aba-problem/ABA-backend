namespace abaproblem.Contracts;

/// <summary>
/// Módulo 3 — Espejo del SELECT final de sp_ObtenerCredencialesBaseDatos.
/// Control 3.2: exposición mínima de credenciales, endpoint separado con rate
/// limit propio; la validación de dueño (control 3.1 BOLA) ya ocurrió dentro del SP.
/// </summary>
public sealed record CredencialDto
{
    public required long Id { get; init; }
    public required string NombreBD { get; init; }
    public required string UsuarioBD { get; init; }
    public required string Password { get; init; }
    public required string Host { get; init; }
    public required int Puerto { get; init; }
    public required string Motor { get; init; }
    public required string Estado { get; init; }
    public required DateTime FechaCreacion { get; init; }
    public DateTime? UltimaActividad { get; init; }
    public required int EspacioMaximoMB { get; init; }
    public required decimal EspacioUtilizadoMB { get; init; }
}
