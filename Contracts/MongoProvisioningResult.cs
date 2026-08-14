namespace abaproblem.Contracts;

/// <summary>
/// Resultado de crear una base en la Mongo Provisioning API. La password viaja en
/// texto plano SOLO dentro de este objeto, en memoria, durante la llamada — el
/// caller (ProvisioningOrchestrator) la pasa como parámetro a sp_ConfirmarAprovisionamientoExterno,
/// que la cifra DENTRO de SQL Server. Nunca se loguea ni se persiste tal cual aquí.
/// Host/Puerto se parsean de connectionString (sin el usuario/password embebidos) —
/// el connectionString completo en sí NUNCA se guarda ni se reconstruye a mano fuera
/// del momento en que el usuario pide ver la credencial (mismo patrón que MySQL/SQLServer).
/// </summary>
public sealed record MongoProvisioningResult(
    string ExternalId,
    string Database,
    string Username,
    string Password,
    string Host,
    int Puerto);
