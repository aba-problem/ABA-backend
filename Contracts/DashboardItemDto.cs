namespace abaproblem.Contracts;

/// <summary>
/// Módulo 3 — Info de conexión/estado de una base aprovisionada, SIN password
/// (eso vive en <see cref="CredencialDto"/>, endpoint separado y rate-limitado).
/// Espejo de sp_ListarBasesDatosUsuario (sql/007_extensiones_backend.sql).
/// </summary>
public sealed record DashboardItemDto
{
    public required long Id { get; init; }
    public required string NombreBD { get; init; }
    public required string UsuarioBD { get; init; }
    public required string Host { get; init; }
    public required int Puerto { get; init; }
    public required string Motor { get; init; }
    public required string Estado { get; init; }
    public required DateTime FechaCreacion { get; init; }
    public DateTime? UltimaActividad { get; init; }
    public required int EspacioMaximoMB { get; init; }
    public required decimal EspacioUtilizadoMB { get; init; }
}
