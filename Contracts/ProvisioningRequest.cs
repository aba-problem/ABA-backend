using System.ComponentModel.DataAnnotations;
using System.Text.Json.Serialization;

namespace abaproblem.Contracts;

/// <summary>
/// Módulo 2 — DTO de entrada del aprovisionamiento.
///
/// Control 2.2: NINGÚN parámetro del cliente puede alterar límites de recursos.
/// El único dato que el cliente elige es el motor destino (MySQL o SQLServer,
/// catálogo real en dbo.MotorBaseDatos); nombre de BD, usuario, contraseña,
/// tamaño (EspacioMaximoMB) y límite de bases se deciden EXCLUSIVAMENTE en
/// sp_AprovisionarBaseDatos.
///
/// El usuarioId NO se acepta aquí (control BOLA): se extrae siempre del claim JWT.
///
/// [JsonExtensionData] captura cualquier propiedad desconocida enviada en el body;
/// combinado con UnmappedMemberHandling.Disallow (Program.cs) y la validación explícita
/// en el controller, se rechaza cualquier intento de mass-assignment / fuzzing de campos.
/// </summary>
public sealed class ProvisioningRequest
{
    /// <summary>Motor destino. Validado contra el catálogo real dbo.MotorBaseDatos por el propio SP.</summary>
    [Required]
    [RegularExpression("^(MySQL|SQLServer)$", ErrorMessage = "Motor no soportado")]
    public string NombreMotor { get; init; } = default!;

    [JsonExtensionData]
    public Dictionary<string, object>? PropiedadesDesconocidas { get; init; }
}
