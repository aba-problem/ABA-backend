using System.Text.Json;

namespace abaproblem.Middleware;

/// <summary>
/// Control 0 — Red de seguridad final: ninguna excepción no controlada debe filtrar
/// stack trace, connection strings, claves JWT, contraseñas ni el mensaje interno de
/// SQL Server/MySQL en la respuesta HTTP — nunca en producción NI en desarrollo, para
/// que el comportamiento no dependa del entorno. Solo un mensaje genérico + un
/// traceId correlacionable con el log detallado del lado servidor.
/// Complementa (no reemplaza) el manejo específico de SpBusinessException/ProvisioningEngineException
/// en cada controller.
/// </summary>
public sealed class ExceptionHandlingMiddleware
{
    private readonly RequestDelegate _next;
    private readonly ILogger<ExceptionHandlingMiddleware> _logger;

    public ExceptionHandlingMiddleware(RequestDelegate next, ILogger<ExceptionHandlingMiddleware> logger)
    {
        _next = next;
        _logger = logger;
    }

    public async Task InvokeAsync(HttpContext context)
    {
        try
        {
            await _next(context);
        }
        catch (Exception ex)
        {
            // TraceIdentifier es único por request (ASP.NET Core lo genera solo) — es la
            // correlación entre "lo que ve el cliente" y "el detalle real en el log del servidor".
            var traceId = context.TraceIdentifier;

            _logger.LogError(ex, "Excepción no controlada método={Metodo} ruta={Ruta} traceId={TraceId}",
                context.Request.Method, context.Request.Path, traceId);

            if (context.Response.HasStarted)
                throw; // ya se escribió parte de la respuesta; no se puede reescribir el status code

            context.Response.Clear();
            context.Response.StatusCode = StatusCodes.Status500InternalServerError;
            context.Response.ContentType = "application/json";
            await context.Response.WriteAsync(
                JsonSerializer.Serialize(new { error = "Ha ocurrido un error interno.", traceId }));
        }
    }
}
