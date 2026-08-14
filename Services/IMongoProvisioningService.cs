using abaproblem.Contracts;

namespace abaproblem.Services;

/// <summary>
/// Tercer motor de aprovisionamiento, contra la Mongo Provisioning API externa
/// (https://mongo.szapatar.dev). Sigue el mismo contrato que IMySqlProvisioningService/
/// ISqlServerProvisioningService: el orquestador decide CUÁNDO llamarlo, este servicio
/// solo sabe hablar con el proveedor.
/// </summary>
public interface IMongoProvisioningService
{
    Task<MongoProvisioningResult> CrearBaseDeDatosAsync(string usuarioBd, CancellationToken ct = default);

    Task<bool> EliminarBaseDeDatosAsync(string mongoExternalId, CancellationToken ct = default);

    /// <summary>
    /// Pide al proveedor credenciales nuevas para una base ya existente. El proveedor
    /// regenera USUARIO Y PASSWORD (no solo la password) — devuelve ambos.
    /// </summary>
    Task<MongoCredencialRotadaResult> RotarCredencialAsync(string mongoExternalId, CancellationToken ct = default);
}
