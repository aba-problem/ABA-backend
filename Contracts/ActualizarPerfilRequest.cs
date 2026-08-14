using System.ComponentModel.DataAnnotations;
using System.Text.Json.Serialization;

namespace abaproblem.Contracts;

/// <summary>
/// DTO de entrada de PUT /dashboard/perfil. La validación de forma acá es defensa
/// temprana; la validación real (nombre vacío tras trim, formato de URL) vive en
/// sp_ActualizarPerfilUsuario — este DTO nunca es la única línea de defensa.
/// </summary>
public sealed class ActualizarPerfilRequest
{
    [Required]
    [StringLength(150, MinimumLength = 1)]
    public string Nombre { get; init; } = default!;

    /// <summary>Null o vacío = vuelve a usar el avatar real de Google/GitHub.</summary>
    [StringLength(500)]
    public string? AvatarUrl { get; init; }

    [JsonExtensionData]
    public Dictionary<string, object>? PropiedadesDesconocidas { get; init; }
}
