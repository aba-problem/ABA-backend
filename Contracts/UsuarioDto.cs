namespace abaproblem.Contracts;

/// <summary>
/// Espejo del SELECT final de sp_CrearUsuario (ABA_Control.dbo.Usuario).
/// Módulo 1: nunca se expone el JWT ni datos sensibles; solo identidad mínima.
/// </summary>
public sealed record UsuarioDto
{
    public required long UsuarioId { get; init; }
    public required string Nombre { get; init; }
    public required string Correo { get; init; }
    public string? AvatarUrl { get; init; }
    public required string Proveedor { get; init; }
    public required DateTime FechaCreacion { get; init; }
    public DateTime? UltimoLogin { get; init; }
}
