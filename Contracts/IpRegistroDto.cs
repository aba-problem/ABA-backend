namespace abaproblem.Contracts;

/// <summary>Espejo del SELECT final de sp_RegistrarIpUsuario. Uso interno (logging/auditoría).</summary>
public sealed record IpRegistroDto
{
    public required long Id { get; init; }
    public required string DireccionIp { get; init; }
    public required string PaisIso { get; init; }
    public required bool Activo { get; init; }
    public DateTime? FechaVerificacion { get; init; }
}
