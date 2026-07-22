namespace abaproblem.Contracts;

/// <summary>
/// Módulo 4 — Solo agregados numéricos (control 4.1). Nunca datos identificables
/// de usuarios individuales (emails, nombres).
/// </summary>
public sealed record MetricasPublicasDto
{
    public required int TotalUsuarios { get; init; }
    public required int TotalBasesCreadas { get; init; }
    public required int BasesActivas { get; init; }
    public required int TotalLogins { get; init; }
    public required int UsuariosActivos30Dias { get; init; }
    public required decimal DisponibilidadPorcentaje { get; init; }
}
