using abaproblem.Contracts;

namespace abaproblem.Repositories.Interfaces;

/// <summary>
/// Módulo 2 — Aprovisionamiento en dos fases (ABA_Control). El backend solo dispara SPs;
/// nombres, tamaños, límites y auditoría los decide SQL Server. La creación DDL real en
/// el motor destino vive en <see cref="abaproblem.Services.IMySqlProvisioningService"/> /
/// <see cref="abaproblem.Services.ISqlServerProvisioningService"/>, orquestada por
/// <see cref="abaproblem.Services.IProvisioningOrchestrator"/>.
/// </summary>
public interface IProvisioningRepository
{
    /// <summary>
    /// Invoca sp_AprovisionarBaseDatos: RESERVA el registro en ABA_Control con
    /// Estado='PENDIENTE' (no crea nada en el motor todavía). El SP genera nombre de
    /// BD/usuario, contraseña segura, valida el límite de bases por usuario y registra
    /// auditoría. Lanza <see cref="SpBusinessException"/> si el SP rechaza (límite excedido,
    /// motor inexistente, etc.).
    /// </summary>
    Task<ProvisioningResultDto> AprovisionarAsync(long usuarioId, string nombreMotor, string? ipOrigen, CancellationToken ct = default);

    /// <summary>
    /// Invoca sp_ConfirmarAprovisionamiento. @exitoso=true → Estado='ACTIVA' (ya existe de
    /// verdad en el motor). @exitoso=false → Estado='ELIMINADA' (nunca queda "activa" sin existir).
    /// </summary>
    Task ConfirmarAsync(long baseDeDatosId, bool exitoso, string? ipOrigen, CancellationToken ct = default);

    /// <summary>Invoca sp_ActualizarEspacioUsado — refleja el uso real medido en el motor y aplica la cuota.</summary>
    Task ActualizarEspacioUsadoAsync(long baseDeDatosId, decimal espacioUtilizadoMB, CancellationToken ct = default);

    /// <summary>Invoca sp_ListarBasesActivasMySql — candidatas al job de enforcement de cuota (solo motor MySQL).</summary>
    Task<IReadOnlyList<BaseParaOperarDto>> ListarActivasMySqlAsync(CancellationToken ct = default);

    /// <summary>
    /// Invoca sp_ConfirmarAprovisionamientoExterno — variante de ConfirmarAsync para motores
    /// (hoy: MongoDB) donde el usuario/password/host reales los decide el proveedor externo,
    /// no SQL Server. @exitoso=true sobreescribe esos valores (cifrando la password DENTRO
    /// del SP) y pasa a 'ACTIVA'; @exitoso=false se comporta igual que ConfirmarAsync.
    /// </summary>
    Task ConfirmarExternoAsync(
        long baseDeDatosId, bool exitoso, string? usuarioBdReal, string? passwordPlano,
        string? host, int? puerto, string? externalId, string? ipOrigen, CancellationToken ct = default);

    /// <summary>
    /// Invoca sp_RotarCredencialExterna — persiste un usuario/password nuevos (ya generados
    /// por un proveedor externo — la Mongo Provisioning API regenera ambos, no solo la
    /// password) para una base activa. BOLA-safe: devuelve false (nunca lanza) si la base
    /// no existe o no pertenece al solicitante — el caller responde 404, igual que RevocarIpAsync.
    /// </summary>
    Task<bool> RotarCredencialExternaAsync(
        long baseDeDatosId, long usuarioIdSolicitante, string usuarioBdNuevo, string passwordPlanoNuevo,
        string? ipOrigen, CancellationToken ct = default);
}
