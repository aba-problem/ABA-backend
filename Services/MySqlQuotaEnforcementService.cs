using abaproblem.Repositories.Interfaces;

namespace abaproblem.Services;

/// <summary>
/// Aplica EspacioMaximoMB en el motor REAL, no solo en ABA_Control. InnoDB no tiene una
/// cuota de disco nativa por schema: no existe una sentencia DDL que "tope" el tamaño
/// de una base MySQL estándar. La forma correcta y real de aplicarlo es a nivel de
/// aplicación: este job mide el uso real vía information_schema.tables y llama a
/// sp_ActualizarEspacioUsado, que decide (en SQL, no en el backend) si debe pausar la
/// BD reutilizando el estado 'PAUSADA' ya existente. Cuando la BD queda PAUSADA, además
/// se revocan privilegios de escritura reales en MySQL — una acción real en el motor,
/// no solo un valor registrado en una tabla de SQL Server.
///
/// Alcance: solo motor MySQL. Las bases del motor SQLServer viven en la MISMA instancia
/// que ABA_Control; medir su uso requeriría sp_spaceused por base, fuera del alcance de
/// Entrega #2 (se documenta como trabajo futuro, no se resuelve con lógica inventada).
/// </summary>
public sealed class MySqlQuotaEnforcementService : BackgroundService
{
    private static readonly TimeSpan Intervalo = TimeSpan.FromMinutes(10);

    private readonly IServiceScopeFactory _scopeFactory;
    private readonly ILogger<MySqlQuotaEnforcementService> _logger;

    public MySqlQuotaEnforcementService(IServiceScopeFactory scopeFactory, ILogger<MySqlQuotaEnforcementService> logger)
    {
        _scopeFactory = scopeFactory;
        _logger = logger;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        using var timer = new PeriodicTimer(Intervalo);
        do
        {
            try
            {
                await AplicarCuotasAsync(stoppingToken);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Ciclo de enforcement de cuota MySQL falló inesperadamente");
            }
        } while (await timer.WaitForNextTickAsync(stoppingToken));
    }

    private async Task AplicarCuotasAsync(CancellationToken ct)
    {
        using var scope = _scopeFactory.CreateScope();
        var repo = scope.ServiceProvider.GetRequiredService<IProvisioningRepository>();
        var mysql = scope.ServiceProvider.GetRequiredService<IMySqlProvisioningService>();

        var bases = await repo.ListarActivasMySqlAsync(ct);
        foreach (var basedatos in bases)
        {
            try
            {
                var usadoMb = await mysql.ObtenerEspacioUsadoMbAsync(basedatos.NombreBD, ct);
                var excedeCuota = usadoMb > basedatos.EspacioMaximoMB;
                var estabaPausada = basedatos.Estado == "PAUSADA";

                if (excedeCuota && !estabaPausada)
                {
                    await mysql.RevocarEscrituraAsync(basedatos.NombreBD, basedatos.UsuarioBD, ct);
                    _logger.LogWarning("Cuota excedida baseId={BaseId} usoMB={UsoMb} maxMB={MaxMb} — escritura revocada",
                        basedatos.Id, usadoMb, basedatos.EspacioMaximoMB);
                }
                else if (!excedeCuota && estabaPausada)
                {
                    await mysql.RestaurarEscrituraAsync(basedatos.NombreBD, basedatos.UsuarioBD, ct);
                    _logger.LogInformation("Cuota restablecida baseId={BaseId} usoMB={UsoMb} maxMB={MaxMb} — escritura restaurada",
                        basedatos.Id, usadoMb, basedatos.EspacioMaximoMB);
                }

                // Fuente de verdad de reporting (Módulo 3 dashboard) + transición de Estado
                // (ACTIVA<->PAUSADA) siempre se decide dentro de sp_ActualizarEspacioUsado.
                await repo.ActualizarEspacioUsadoAsync(basedatos.Id, usadoMb, ct);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Fallo aplicando cuota baseId={BaseId}", basedatos.Id);
            }
        }
    }
}
