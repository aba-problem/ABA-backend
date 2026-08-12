namespace abaproblem.Contracts;

/// <summary>Respuesta paginada de sp_ListarSesionesUsuario.</summary>
public sealed record SesionesPaginadasDto
{
    public required IReadOnlyList<SesionRegistroDto> Registros { get; init; }
    public required int Total { get; init; }
    public required int Pagina { get; init; }
    public required int TamanoPagina { get; init; }
}
