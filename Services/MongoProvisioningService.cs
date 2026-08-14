using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using abaproblem.Contracts;

namespace abaproblem.Services;

/// <summary>
/// Implementación real contra la Mongo Provisioning API (https://mongo.szapatar.dev).
/// El HttpClient tipado (Program.cs) ya trae BaseAddress y el header X-API-Key con la
/// key admin del backend — nunca se concatena ni se loguea aquí.
///
/// Control crítico: la respuesta del proveedor incluye "connectionString" con la
/// password en texto plano embebida (mongodb://user:pass@host:port/db). Ese string
/// NUNCA se persiste ni se loguea tal cual — se descompone en host/puerto/usuario/
/// password inmediatamente (usuario/password ya vienen sueltos en el JSON de todos
/// modos; solo host/puerto se extraen del connectionString) y se descarta.
/// </summary>
public sealed class MongoProvisioningService : IMongoProvisioningService
{
    private readonly HttpClient _http;
    private readonly ILogger<MongoProvisioningService> _logger;

    public MongoProvisioningService(HttpClient http, ILogger<MongoProvisioningService> logger)
    {
        _http = http;
        _logger = logger;
    }

    public async Task<MongoProvisioningResult> CrearBaseDeDatosAsync(string usuarioBd, CancellationToken ct = default)
    {
        HttpResponseMessage respuesta;
        try
        {
            respuesta = await _http.PostAsJsonAsync("databases", new { username = usuarioBd }, ct);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _logger.LogError(ex, "Fallo de red creando base en Mongo Provisioning API");
            throw new ProvisioningEngineException("No se pudo aprovisionar la base MongoDB en el motor destino.", ex);
        }

        using (respuesta)
        {
            if (respuesta.StatusCode == HttpStatusCode.TooManyRequests)
            {
                _logger.LogWarning("Mongo Provisioning API respondió 429 (límite del proveedor alcanzado)");
                throw new ProvisioningEngineException(
                    "No se pudo aprovisionar la base MongoDB en el motor destino.",
                    new ServicioExternoSaturadoException("MongoProvisioningApi"));
            }

            if (!respuesta.IsSuccessStatusCode)
            {
                _logger.LogError("Mongo Provisioning API respondió status {Status} creando base", (int)respuesta.StatusCode);
                throw new ProvisioningEngineException(
                    "No se pudo aprovisionar la base MongoDB en el motor destino.",
                    new HttpRequestException($"Mongo Provisioning API status {(int)respuesta.StatusCode}"));
            }

            await using var stream = await respuesta.Content.ReadAsStreamAsync(ct);
            using var json = await JsonDocument.ParseAsync(stream, cancellationToken: ct);
            var root = json.RootElement;

            var externalId = root.GetProperty("id").GetString()!;
            var database = root.GetProperty("database").GetString()!;
            var username = root.GetProperty("username").GetString()!;
            var password = root.GetProperty("password").GetString()!;
            var connectionString = root.TryGetProperty("connectionString", out var cs) ? cs.GetString() : null;

            var (host, puerto) = ExtraerHostYPuerto(connectionString);

            _logger.LogInformation("Base MongoDB creada externalId={ExternalId} database={Database}", externalId, database);

            return new MongoProvisioningResult(externalId, database, username, password, host, puerto);
        }
    }

    public async Task<bool> EliminarBaseDeDatosAsync(string mongoExternalId, CancellationToken ct = default)
    {
        try
        {
            using var respuesta = await _http.DeleteAsync($"databases/{mongoExternalId}", ct);
            if (!respuesta.IsSuccessStatusCode)
            {
                _logger.LogError("Mongo Provisioning API rechazó la eliminación de {ExternalId} (status {Status})",
                    mongoExternalId, (int)respuesta.StatusCode);
                return false;
            }
            return true;
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _logger.LogError(ex, "Fallo de red eliminando base Mongo {ExternalId}", mongoExternalId);
            return false;
        }
    }

    public async Task<MongoCredencialRotadaResult> RotarCredencialAsync(string mongoExternalId, CancellationToken ct = default)
    {
        HttpResponseMessage respuesta;
        try
        {
            respuesta = await _http.PostAsync($"databases/{mongoExternalId}/credentials/reset", content: null, ct);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _logger.LogError(ex, "Fallo de red rotando credencial Mongo {ExternalId}", mongoExternalId);
            throw new ProvisioningEngineException("No se pudo rotar la credencial en el motor destino.", ex);
        }

        using (respuesta)
        {
            if (respuesta.StatusCode == HttpStatusCode.TooManyRequests)
                throw new ServicioExternoSaturadoException("MongoProvisioningApi");

            if (!respuesta.IsSuccessStatusCode)
            {
                _logger.LogError("Mongo Provisioning API respondió status {Status} rotando credencial {ExternalId}",
                    (int)respuesta.StatusCode, mongoExternalId);
                throw new ProvisioningEngineException(
                    "No se pudo rotar la credencial en el motor destino.",
                    new HttpRequestException($"Mongo Provisioning API status {(int)respuesta.StatusCode}"));
            }

            await using var stream = await respuesta.Content.ReadAsStreamAsync(ct);
            using var json = await JsonDocument.ParseAsync(stream, cancellationToken: ct);
            var root = json.RootElement;

            // ResetCredentialsResponse (OpenAPI real del proveedor): database, username,
            // password, connectionString, rotatedAt — regenera USUARIO Y password, no solo
            // la password. connectionString se ignora (mismo motivo que en CrearBaseDeDatosAsync).
            var usernameNuevo = root.GetProperty("username").GetString()!;
            var passwordNueva = root.GetProperty("password").GetString()!;

            _logger.LogInformation("Credencial Mongo rotada externalId={ExternalId}", mongoExternalId);
            return new MongoCredencialRotadaResult(usernameNuevo, passwordNueva);
        }
    }

    /// <summary>
    /// Único uso permitido del connectionString crudo: leer host/puerto para mostrarlos
    /// en el dashboard. Se descarta inmediatamente después — nunca se guarda la variable
    /// que lo contiene más allá de este método, y nunca se loguea su valor.
    /// </summary>
    private (string Host, int Puerto) ExtraerHostYPuerto(string? connectionString)
    {
        const string hostPorDefecto = "mongo.szapatar.dev";
        const int puertoPorDefecto = 27017;

        if (string.IsNullOrWhiteSpace(connectionString))
            return (hostPorDefecto, puertoPorDefecto);

        try
        {
            var uri = new Uri(connectionString);
            var host = uri.Host;
            var puerto = uri.Port > 0 ? uri.Port : puertoPorDefecto;
            return (string.IsNullOrEmpty(host) ? hostPorDefecto : host, puerto);
        }
        catch (UriFormatException)
        {
            _logger.LogWarning("No se pudo parsear host/puerto del connectionString devuelto por Mongo (formato inesperado)");
            return (hostPorDefecto, puertoPorDefecto);
        }
    }
}
