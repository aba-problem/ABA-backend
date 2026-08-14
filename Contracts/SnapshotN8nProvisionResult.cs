namespace abaproblem.Contracts;

/// <summary>
/// Resultado de POST /n8n/external/provision en la API de Snapshot. A diferencia de
/// MySQL/SQLServer/Mongo, no hay password — Credential es un enlace de invitación de
/// un solo uso (Snapshot no permite fijar contraseña vía API).
/// </summary>
public sealed record SnapshotN8nProvisionResult(string AccountId, string Status, string AccessType, string Credential);
