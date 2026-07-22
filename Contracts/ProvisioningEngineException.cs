namespace abaproblem.Contracts;

/// <summary>
/// La reserva en ABA_Control (Estado='PENDIENTE') se confirmó, pero la creación
/// real en el motor destino (MySQL o SQLServer) falló. El orquestador ya llamó a
/// sp_ConfirmarAprovisionamiento con @Exitoso=0 (la fila queda 'ELIMINADA') antes de
/// lanzar esta excepción — el controller solo la traduce a una respuesta HTTP sin
/// exponer el detalle interno del motor.
/// </summary>
public sealed class ProvisioningEngineException : Exception
{
    public ProvisioningEngineException(string message, Exception innerException)
        : base(message, innerException)
    {
    }
}
