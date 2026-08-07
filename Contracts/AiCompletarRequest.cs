using System.ComponentModel.DataAnnotations;
using System.Text.Json.Serialization;

namespace abaproblem.Contracts;

/// <summary>
/// Módulo IA como Servicio — DTO de entrada de /ai/completar. Este entregable cubre
/// solo el andamiaje (auth ApiKey, rate limit, auditoría de consumo); la integración
/// con un proveedor de IA real queda documentada como pendiente, no bloqueante.
/// </summary>
public sealed class AiCompletarRequest
{
    [Required]
    [StringLength(4000, MinimumLength = 1)]
    public string Prompt { get; init; } = default!;

    [JsonExtensionData]
    public Dictionary<string, object>? PropiedadesDesconocidas { get; init; }
}
