using abaproblem.Contracts;

namespace abaproblem.Repositories.Interfaces;

/// <summary>
/// Módulo 8 — Acceso a datos de células socias. El backend NO decide si una API key es
/// válida ni cuál es el prefijo de la célula: esta interfaz solo describe la invocación
/// de sp_ValidarApiKeyCelula (ABA_Control). Esa decisión vive enteramente en el SP.
/// </summary>
public interface ICelulaSociaRepository
{
    /// <summary>
    /// Invoca sp_ValidarApiKeyCelula con el hash SHA-256 de la API key recibida.
    /// Devuelve null si la key no existe o la célula está desactivada — el backend nunca
    /// distingue el motivo exacto en la respuesta HTTP.
    /// </summary>
    Task<CelulaSociaDto?> ValidarApiKeyAsync(byte[] apiKeyHash, CancellationToken ct = default);
}
