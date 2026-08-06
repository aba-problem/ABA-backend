namespace abaproblem.Contracts;

/// <summary>
/// Resultado de la fase 1 (sp_ValidarYCrearRegistroDns, estado PENDIENTE) y también lo
/// que devuelven sp_EliminarRegistroDns/sp_EliminarRegistroDnsAdmin — el backend necesita
/// Subdominio/TipoRegistro/Valor para la llamada de confirmación al proveedor DNS real.
/// </summary>
public sealed record DnsRegistroReservaDto
{
    public required int Id { get; init; }
    public required string Subdominio { get; init; }
    public required string TipoRegistro { get; init; }
    public required string Valor { get; init; }
}
