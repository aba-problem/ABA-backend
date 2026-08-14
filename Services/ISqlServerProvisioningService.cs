namespace abaproblem.Services;

/// <summary>
/// Motor 'SQLServer' del catálogo dbo.MotorBaseDatos.
///
/// EN TRANSICIÓN hacia una instancia externa ofrecida por una célula socia
/// (SqlServerExterno:AdminConnectionString, mismo espíritu que MySql:AdminConnectionString).
/// Mientras esa variable no esté configurada, la implementación real (SqlServerProvisioningService)
/// sigue usando la instancia propia — la MISMA que ABA_Control — igual que siempre funcionó: por eso
/// hoy protegen a este motor el logon trigger de sql/004_logon_trigger_sqlserver.sql (whitelist de IP)
/// y el Resource Governor de sql/006 (límites de conexión/CPU), ambos pensados para un motor
/// COMPARTIDO con ABA_Control.
///
/// Cuando se configure SqlServerExterno:AdminConnectionString con los datos reales de la célula, el
/// switch es automático (sin tocar código) — pero ese día, NINGUNA de esas dos protecciones va a
/// aplicar más (viven en nuestra instancia, no en la de ellos): quedaría sin whitelist de IP para
/// bases 'SQLServer' de estudiantes, mismo gap ya aceptado y documentado para MongoDB
/// (Aba/API-CELULAS-SOCIAS.md § 7 — "no implementada, cualquiera con la contraseña correcta se
/// conecta desde donde sea"). Pendiente evaluar en ese momento si la célula que ofrece el servicio
/// tiene su propio control equivalente, o si hace falta pedirlo.
/// </summary>
public interface ISqlServerProvisioningService
{
    /// <summary>Crea el LOGIN, la DATABASE y deja al login como db_owner únicamente de esa BD.</summary>
    Task CrearBaseDeDatosAsync(string nombreBD, string usuarioBD, string password, CancellationToken ct = default);
}
