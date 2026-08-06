namespace abaproblem.Contracts;

/// <summary>Espejo de sp_ObtenerMiWorkspace — nunca incluye la contraseña.</summary>
public sealed record N8nWorkspaceDto
{
    public required int Id { get; init; }
    public required string NombreWorkspace { get; init; }
    public required int LimiteWorkflows { get; init; }
    public required int LimiteEjecucionesMes { get; init; }
    public required string Estado { get; init; }
    public required DateTime FechaCreacion { get; init; }
}
