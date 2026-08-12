using System.ComponentModel.DataAnnotations;
using System.Text.Json.Serialization;

namespace abaproblem.Contracts;

/// <summary>DTO de entrada de POST /sesiones/ips/revocar ("No fui yo, bloquear").</summary>
public sealed class RevocarIpRequest
{
    [Required]
    [StringLength(45, MinimumLength = 3)]
    public string DireccionIp { get; init; } = default!;

    [JsonExtensionData]
    public Dictionary<string, object>? PropiedadesDesconocidas { get; init; }
}
