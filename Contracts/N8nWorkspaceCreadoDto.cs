namespace abaproblem.Contracts;

/// <summary>
/// Espejo de sp_CrearWorkspaceN8N. PasswordTemporal viaja UNA sola vez en este body
/// HTTPS — nunca en query string, nunca en un link, nunca logueado (control 5.8).
/// </summary>
public sealed record N8nWorkspaceCreadoDto
{
    public required int Id { get; init; }
    public required string NombreWorkspace { get; init; }
    public required string PasswordTemporal { get; init; }
    public required int LimiteWorkflows { get; init; }
    public required int LimiteEjecucionesMes { get; init; }
}
