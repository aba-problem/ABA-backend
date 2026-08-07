namespace abaproblem.Contracts;

/// <summary>Módulo 8 — Espejo de sp_ObtenerBaseDatosSocioPorId (dbo.BaseDeDatosSocio). Sin contraseña: eso solo viaja una vez, en ProvisioningResultDto al crear.</summary>
public sealed record BaseDatosSocioDto
{
    public required long Id { get; init; }
    public required string NombreBD { get; init; }
    public required string UsuarioBD { get; init; }
    public required string Host { get; init; }
    public required int Puerto { get; init; }
    public required string Estado { get; init; }
    public required int EspacioMaximoMB { get; init; }
    public required decimal EspacioUtilizadoMB { get; init; }
    public required DateTime FechaCreacion { get; init; }
}
