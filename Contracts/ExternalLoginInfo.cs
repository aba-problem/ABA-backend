using System.ComponentModel.DataAnnotations;

namespace abaproblem.Contracts;

/// <summary>
/// Control 1.4 — Payload de retorno de OAuth validado ANTES de pasarlo a sp_CrearUsuario.
/// Nunca se confía ciegamente en los claims de Google/GitHub: se valida formato,
/// longitud y (en Google) email_verified. Los límites de longitud previenen
/// payloads anómalos / fuzzing vía claims manipulados.
/// </summary>
public sealed class ExternalLoginInfo
{
    [Required]
    [RegularExpression("^(GOOGLE|GITHUB)$", ErrorMessage = "Proveedor no soportado")]
    public string Proveedor { get; init; } = default!; // 'GOOGLE' | 'GITHUB' — coincide con CK_Usuario_Proveedor

    /// <summary>El "sub" (Google) o "id" (GitHub) devuelto por el proveedor — identifica la cuenta de forma estable.</summary>
    [Required]
    [StringLength(100, MinimumLength = 1)]
    public string ProveedorUsuarioId { get; init; } = default!;

    [Required]
    [EmailAddress]
    [StringLength(255, MinimumLength = 3)]
    public string Correo { get; init; } = default!;

    [Required]
    [StringLength(150, MinimumLength = 1)]
    public string Nombre { get; init; } = default!;

    [StringLength(500)]
    [RegularExpression(@"^https?://.+", ErrorMessage = "avatar_url con formato inválido")]
    public string? AvatarUrl { get; init; }

    /// <summary>Solo aplica a Google; GitHub no expone email_verified de la misma forma.</summary>
    public bool EmailVerificado { get; init; }
}
