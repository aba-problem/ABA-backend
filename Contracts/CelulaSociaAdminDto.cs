namespace abaproblem.Contracts;

/// <summary>
/// Espejo de sp_ListarCelulasSocias / sp_CambiarEstadoCelulaSocia (panel de admin, sql/018).
/// Distinto de CelulaSociaDto (ese es el resultado de sp_ValidarApiKeyCelula, usado en la
/// autenticación por API key de la propia célula — otro flujo, otra identidad). Nunca incluye
/// la API key.
/// </summary>
public sealed record CelulaSociaResumenDto
{
    public required int Id { get; init; }
    public required string NombreCelula { get; init; }
    public required string Prefijo { get; init; }
    public required bool Activo { get; init; }
    public required DateTime FechaCreacion { get; init; }
}

/// <summary>
/// Espejo de sp_AltaCelulaSociaAutoKey / sp_RotarApiKeyCelulaSocia. Incluye la API key en
/// texto plano — se entrega UNA SOLA VEZ en esta respuesta, igual que passwordTemporal al
/// aprovisionar una base. Nunca se vuelve a poder leer después.
/// </summary>
public sealed record CelulaSociaCreadaDto
{
    public required int Id { get; init; }
    public required string NombreCelula { get; init; }
    public required string Prefijo { get; init; }
    public required bool Activo { get; init; }
    public required DateTime FechaCreacion { get; init; }
    public required string ApiKey { get; init; }
}

/// <summary>Body de POST /admin/celulas-socias.</summary>
public sealed record AltaCelulaSociaRequest
{
    public required string NombreCelula { get; init; }
    public required string Prefijo { get; init; }
}

/// <summary>Body de PATCH /admin/celulas-socias/{id}/estado.</summary>
public sealed record CambiarEstadoCelulaSociaRequest
{
    public required bool Activo { get; init; }
}
