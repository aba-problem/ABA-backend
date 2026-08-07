namespace abaproblem.Contracts;

/// <summary>Espejo de sp_ListarApiKeys — solo el prefijo, nunca la key completa ni el hash.</summary>
public sealed record ApiKeyDto
{
    public required int Id { get; init; }
    public required string Prefijo { get; init; }
    public required bool Activa { get; init; }
    public required DateTime FechaCreacion { get; init; }
    public DateTime? FechaRevocacion { get; init; }
    public DateTime? UltimoUso { get; init; }
}
