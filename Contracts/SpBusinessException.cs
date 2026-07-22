namespace abaproblem.Contracts;

/// <summary>
/// Regla de oro: la lógica de negocio vive en SQL Server. Cuando un SP rechaza una
/// operación (p.ej. límite de bases excedido — control 2.2), lanza un error estructurado
/// vía THROW con un número de error &gt;= 50000. El repositorio lo traduce a esta excepción
/// para que el controller la mapee a un status HTTP, SIN que el backend contenga la regla.
/// </summary>
public sealed class SpBusinessException : Exception
{
    /// <summary>Número de error del SP (SQL error number, normalmente 50000+).</summary>
    public int SpErrorNumber { get; }

    public SpBusinessException(int spErrorNumber, string message) : base(message)
    {
        SpErrorNumber = spErrorNumber;
    }
}
