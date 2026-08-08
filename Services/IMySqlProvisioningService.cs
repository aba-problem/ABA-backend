namespace abaproblem.Services;

/// <summary>
/// Ejecuta el DDL REAL contra el motor MySQL. Nunca se invoca antes de que
/// sp_AprovisionarBaseDatos confirme la reserva en ABA_Control (Estado='PENDIENTE');
/// el orquestador decide cuándo llamarlo.
/// </summary>
public interface IMySqlProvisioningService
{
    /// <summary>
    /// Crea la base de datos, el usuario y sus permisos acotados en MySQL.
    /// Si falla a mitad de camino, intenta limpiar lo que alcanzó a crear antes de relanzar
    /// (para que un reintento posterior con el mismo nombre no choque con un objeto huérfano).
    /// </summary>
    Task CrearBaseDeDatosAsync(string nombreBaseDatos, string usuarioBaseDatos, string password, CancellationToken ct = default);

    /// <summary>Espacio real usado por el schema en MySQL (MB), leído de information_schema.</summary>
    Task<int> ObtenerEspacioUsadoMbAsync(string nombreBaseDatos, CancellationToken ct = default);

    /// <summary>Control 2.2 (cuota real) — revoca escritura, deja al usuario en solo-lectura.</summary>
    Task RevocarEscrituraAsync(string nombreBaseDatos, string usuarioBaseDatos, CancellationToken ct = default);

    /// <summary>Restaura la escritura cuando el uso vuelve a estar dentro de la cuota.</summary>
    Task RestaurarEscrituraAsync(string nombreBaseDatos, string usuarioBaseDatos, CancellationToken ct = default);

    /// <summary>
    /// Módulo 8 (desaprovisionamiento real de células socias) — DROP DATABASE + DROP USER en
    /// MySQL y limpieza del registro correspondiente en ProxySQL. A diferencia del "desactivar"
    /// de estudiantes (soft-delete puro en ABA_Control, la BD real nunca se toca), este método
    /// SÍ elimina la BD real — pensado para cuando un usuario final de una célula socia se da de
    /// baja y esa célula llama DELETE explícitamente.
    /// </summary>
    Task EliminarBaseDeDatosAsync(string nombreBaseDatos, string usuarioBaseDatos, CancellationToken ct = default);

    /// <summary>
    /// Módulo 8 (reset de credenciales de células socias) — aplica una contraseña nueva al
    /// usuario ya existente (ALTER USER, no CREATE) y la sincroniza en ProxySQL. La base y el
    /// usuario no se tocan, solo la contraseña.
    /// </summary>
    Task RotarPasswordAsync(string usuarioBaseDatos, string nuevaPassword, CancellationToken ct = default);
}
