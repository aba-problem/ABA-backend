namespace abaproblem.Contracts;

/// <summary>
/// Proyección mínima de una base ACTIVA en motor MySQL, para el job en background
/// de enforcement de cuota (Services/MySqlQuotaEnforcementService.cs). No se expone vía HTTP.
/// Espejo de sp_ListarBasesActivasMySql (sql/007_extensiones_backend.sql).
/// </summary>
public sealed record BaseParaOperarDto
{
    public required long Id { get; init; }
    public required long UsuarioId { get; init; }
    public required string NombreBD { get; init; }
    public required string UsuarioBD { get; init; }
    public required int EspacioMaximoMB { get; init; }
    public required decimal EspacioUtilizadoMB { get; init; }
    public required string Estado { get; init; }
}
