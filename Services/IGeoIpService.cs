namespace abaproblem.Services;

/// <summary>
/// Resuelve el país (ISO-3166-1 alpha-2) de una IP. SQL Server no puede hacer
/// resolución geo-IP por sí mismo — esta es la única pieza de "decisión" que
/// físicamente tiene que vivir en el backend; la REGLA de qué países se permiten
/// sigue siendo 100% de la base de datos (FK UsuarioIp → PaisPermitido).
/// </summary>
public interface IGeoIpService
{
    /// <summary>Null si no se pudo resolver (IP privada/local, proveedor caído, timeout, etc.).</summary>
    Task<string?> ResolverPaisIsoAsync(string direccionIp, CancellationToken ct = default);
}
