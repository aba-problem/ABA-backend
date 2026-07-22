namespace abaproblem.Services;

/// <summary>
/// Motor 'SQLServer' del catálogo dbo.MotorBaseDatos: la BD del estudiante vive en la
/// MISMA instancia física que ABA_Control (por eso existen el logon trigger de
/// sql/004_logon_trigger_sqlserver.sql y el Resource Governor de sql/006 — protegen
/// al motor compartido de estas BDs de estudiantes).
/// </summary>
public interface ISqlServerProvisioningService
{
    /// <summary>Crea el LOGIN, la DATABASE y deja al login como db_owner únicamente de esa BD.</summary>
    Task CrearBaseDeDatosAsync(string nombreBD, string usuarioBD, string password, CancellationToken ct = default);
}
