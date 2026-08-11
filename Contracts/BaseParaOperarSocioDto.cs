namespace abaproblem.Contracts;

/// <summary>
/// Proyección mínima de una base de célula socia candidata al job de enforcement de
/// cuota (Services/MySqlQuotaEnforcementService.cs). No se expone vía HTTP. Espejo de
/// BaseParaOperarDto (bases de estudiantes), con CelulaSociaId en vez de UsuarioId.
/// Espejo de sp_ListarBasesActivasMySqlSocio (sql/017_espacio_socio.sql).
/// </summary>
public sealed record BaseParaOperarSocioDto
{
    public required long Id { get; init; }
    public required int CelulaSociaId { get; init; }
    public required string NombreBD { get; init; }
    public required string UsuarioBD { get; init; }
    public required int EspacioMaximoMB { get; init; }
    public required decimal EspacioUtilizadoMB { get; init; }
    public required string Estado { get; init; }
}
