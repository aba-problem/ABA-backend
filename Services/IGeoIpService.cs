namespace abaproblem.Services;

/// <summary>País + ciudad resueltos para una IP. Ciudad es solo informativa (se muestra en
/// Registros de sesión); ninguna regla de negocio depende de ella, a diferencia de PaisIso.</summary>
public sealed record GeoUbicacion(string PaisIso, string? Ciudad);

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

    /// <summary>
    /// Igual que <see cref="ResolverPaisIsoAsync"/> pero además trae la ciudad cuando el
    /// proveedor la resuelve — puramente para mostrarla en Registros de sesión (control 5.8
    /// no depende de esto). Null en las mismas condiciones que el método anterior.
    /// </summary>
    Task<GeoUbicacion?> ResolverUbicacionAsync(string direccionIp, CancellationToken ct = default);
}
