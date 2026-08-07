using System.Text.Json.Serialization;

namespace abaproblem.Contracts;

/// <summary>
/// DTO de entrada para endpoints POST de creación que no reciben ningún campo del
/// cliente (N8N y ApiKeys: nombre/contraseña/key se generan enteramente en el SP).
/// Existe solo para que UnmappedMemberHandling.Disallow + PropiedadesDesconocidas
/// rechacen cualquier intento de mass-assignment si alguien envía un body inesperado
/// (control 2.2, mismo patrón que ProvisioningRequest).
/// </summary>
public sealed class CrearSinCuerpoRequest
{
    [JsonExtensionData]
    public Dictionary<string, object>? PropiedadesDesconocidas { get; init; }
}
