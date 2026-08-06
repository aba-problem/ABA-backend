namespace abaproblem.Contracts;

/// <summary>Espejo de sp_ObtenerConsumoApiKey — agregado por día, últimos 30 días.</summary>
public sealed record ApiKeyConsumoDiaDto
{
    public required DateTime Dia { get; init; }
    public required int Llamadas { get; init; }
    public required int TokensTotales { get; init; }
}
