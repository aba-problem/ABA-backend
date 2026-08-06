namespace abaproblem.Contracts;

/// <summary>
/// Interno — nunca sale por la API. Fila candidata que devuelve
/// sp_ObtenerApiKeyPorPrefijo para que <see cref="abaproblem.Services.ApiKeyAuthenticationHandler"/>
/// compare el hash en tiempo constante fuera de SQL.
/// </summary>
public sealed record ApiKeyCandidataDto
{
    public required int Id { get; init; }
    public required long UsuarioId { get; init; }
    public required byte[] KeyHash { get; init; }
    public required bool Activa { get; init; }
}
