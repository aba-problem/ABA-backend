namespace abaproblem.Middleware;

/// <summary>
/// Control 5.8 — Logging transversal de auditoría técnica: IP, endpoint, usuarioId (si aplica),
/// resultado (status code) y timestamp. NUNCA loguea el JWT completo, contraseñas generadas,
/// connection strings ni headers Authorization crudos — la auditoría de NEGOCIO (creación de
/// base, cambios de permisos) vive en SQL Server vía los SPs correspondientes (tabla Auditoria).
/// </summary>
public sealed class RequestAuditMiddleware
{
    private readonly RequestDelegate _next;
    private readonly ILogger<RequestAuditMiddleware> _logger;

    public RequestAuditMiddleware(RequestDelegate next, ILogger<RequestAuditMiddleware> logger)
    {
        _next = next;
        _logger = logger;
    }

    public async Task InvokeAsync(HttpContext context)
    {
        // Se registra en el "next()" para que, al retornar, ya corrieron Authentication/
        // Authorization/Controllers y contamos con context.User y el status code final.
        await _next(context);

        var usuarioId = context.User.FindFirst("sub")?.Value ?? "anon";

        _logger.LogInformation(
            "ip={Ip} metodo={Metodo} ruta={Ruta} usuarioId={UsuarioId} status={Status} ts={Timestamp:o}",
            context.Connection.RemoteIpAddress, context.Request.Method, context.Request.Path,
            usuarioId, context.Response.StatusCode, DateTimeOffset.UtcNow);
    }
}
