namespace abaproblem.Contracts;

/// <summary>
/// Resultado de POST /databases/{id}/credentials/reset en la Mongo Provisioning API.
/// Confirmado contra el OpenAPI real del proveedor (ResetCredentialsResponse): el reset
/// regenera USUARIO Y PASSWORD, no solo la password — hay que persistir ambos o UsuarioBD
/// queda desincronizado del usuario real en el proveedor.
/// </summary>
public sealed record MongoCredencialRotadaResult(string Username, string Password);
