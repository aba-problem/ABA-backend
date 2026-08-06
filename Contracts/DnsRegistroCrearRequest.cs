using System.ComponentModel.DataAnnotations;
using System.Text.Json.Serialization;

namespace abaproblem.Contracts;

/// <summary>
/// Módulo DNS — DTO de entrada de /dns/crear. A diferencia de N8N/BD (donde el nombre
/// lo genera el SP), aquí el usuario SÍ elige el subdominio — por diseño del entregable.
/// La validación de forma aquí es defensa temprana (rechaza antes de tocar la BD); la
/// validación real y definitiva (colisión, límite, regex) vive en
/// sp_ValidarYCrearRegistroDns — este DTO nunca es la única línea de defensa.
/// </summary>
public sealed class DnsRegistroCrearRequest
{
    [Required]
    [RegularExpression("^[a-z0-9-]{1,40}$", ErrorMessage = "Subdominio inválido: solo minúsculas, dígitos y guiones, máx 40 caracteres.")]
    public string Subdominio { get; init; } = default!;

    [Required]
    [RegularExpression("^(A|CNAME)$", ErrorMessage = "Tipo de registro no soportado.")]
    public string TipoRegistro { get; init; } = default!;

    [Required]
    [StringLength(255, MinimumLength = 1)]
    public string Valor { get; init; } = default!;

    [JsonExtensionData]
    public Dictionary<string, object>? PropiedadesDesconocidas { get; init; }
}
