namespace abaproblem.Contracts;

/// <summary>
/// Espejo de sp_RegistrarWorkspaceN8NExterno. A diferencia de las bases de datos, Snapshot
/// (proveedor real de N8N) no permite fijar contraseña vía API — CredencialUrl es un
/// ENLACE DE INVITACIÓN de un solo uso, no una contraseña. Igual que una password, viaja
/// UNA sola vez en este body HTTPS — nunca en query string, nunca logueado (control 5.8).
/// </summary>
public sealed record N8nWorkspaceCreadoDto
{
    public required int Id { get; init; }
    public required string NombreWorkspace { get; init; }
    public required string CredencialUrl { get; init; }
    public required int LimiteWorkflows { get; init; }
    public required int LimiteEjecucionesMes { get; init; }
}
