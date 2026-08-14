namespace abaproblem.Contracts;

/// <summary>
/// Un proveedor externo (PolyService IA, Mongo Provisioning API) respondió 429 —
/// alcanzó su propio límite de uso. Distinta de <see cref="ProvisioningEngineException"/>:
/// esta es transitoria y específica de "saturado ahora", no un fallo de aprovisionamiento
/// que requiera revertir una reserva en ABA_Control.
/// </summary>
public sealed class ServicioExternoSaturadoException : Exception
{
    public string Servicio { get; }

    public ServicioExternoSaturadoException(string servicio)
        : base($"El servicio externo '{servicio}' alcanzó su límite de uso.")
    {
        Servicio = servicio;
    }
}
