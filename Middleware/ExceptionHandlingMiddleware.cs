using System.Text.Json;
using Microsoft.AspNetCore.Antiforgery;

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
        catch (AntiforgeryValidationException ex)
        {
            // Error de CLIENTE esperable (falta/no coincide X-CSRF-TOKEN) — no es una falla
            // del servidor. 400, no 500; se loguea como warning (no "excepción no controlada")
            // para no ensuciar el log de producción con algo que el frontend puede corregir
            // llamando primero a GET /auth/csrf.
            var traceId = context.TraceIdentifier;
            _logger.LogWarning("CSRF inválido/ausente método={Metodo} ruta={Ruta} traceId={TraceId} detalle={Detalle}",
                context.Request.Method, context.Request.Path, traceId, ex.Message);

            if (context.Response.HasStarted)
                throw;

            context.Response.Clear();
            context.Response.StatusCode = StatusCodes.Status400BadRequest;
            context.Response.ContentType = "application/json";
            await context.Response.WriteAsync(JsonSerializer.Serialize(new
            {
                error = "CSRF_INVALIDO",
                mensaje = "Token CSRF ausente o inválido. Llama a GET /auth/csrf y reenvía su valor en el header X-CSRF-TOKEN.",
                traceId,
            }));
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
