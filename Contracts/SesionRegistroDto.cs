namespace abaproblem.Contracts;

/// <summary>Espejo de sp_ListarSesionesUsuario — historial de acceso del propio usuario.</summary>
public sealed record SesionRegistroDto
{
    public required long Id { get; init; }
    public required string Entidad { get; init; }
    public required string Accion { get; init; }
    public string? IpOrigen { get; init; }
    public required DateTime FechaEvento { get; init; }
    public string? Detalle { get; init; }
}
