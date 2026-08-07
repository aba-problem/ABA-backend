namespace abaproblem.Contracts;

/// <summary>Módulo 8 — Resultado de sp_ValidarApiKeyCelula (ABA_Control.dbo.CelulasSocias).</summary>
public sealed class CelulaSociaDto
{
    public required int CelulaId { get; init; }
    public required string NombreCelula { get; init; }
    public required string Prefijo { get; init; }
}
