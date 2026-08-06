namespace abaproblem.Contracts;

/// <summary>
/// Espejo de sp_CrearApiKey. KeyCompleta viaja UNA sola vez en este body HTTPS — el
/// backend solo guarda el hash; no hay forma de volver a consultarla (control 5.8).
/// </summary>
public sealed record ApiKeyCreadaDto
{
    public required int Id { get; init; }
    public required string Prefijo { get; init; }
    public required string KeyCompleta { get; init; }
    public required DateTime FechaCreacion { get; init; }
}
