namespace abaproblem.Contracts;

/// <summary>
/// Espejo de sp_ListarMisRegistrosDns / sp_ListarTodosRegistrosDns. UsuarioId y
/// UsuarioCorreo solo se pueblan en el listado admin (control 4.1 del módulo original:
/// nunca exponer identificadores de otros usuarios en un endpoint que no lo necesite).
/// </summary>
public sealed record DnsRegistroDto
{
    public required int Id { get; init; }
    public int? UsuarioId { get; init; }
    public string? UsuarioCorreo { get; init; }
    public required string Subdominio { get; init; }
    public required string TipoRegistro { get; init; }
    public required string Valor { get; init; }
    public required string Estado { get; init; }
    public required DateTime FechaCreacion { get; init; }
}
