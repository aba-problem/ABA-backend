namespace abaproblem.Contracts;

/// <summary>
/// Snapshot respondió 409 al aprovisionar: ya existe una cuenta de N8N para ese
/// external_user_ref/correo del lado del proveedor. Distinta del 409 "propio" de ABA
/// (sp_RegistrarWorkspaceN8NExterno, 50020 — ya hay un workspace ACTIVO en ABA_Control):
/// esta representa una desincronización real con el proveedor externo (p. ej. el usuario
/// "eliminó" su workspace en ABA — soft delete local, sin endpoint de borrado en
/// Snapshot — y ahora intenta crear uno nuevo).
/// </summary>
public sealed class SnapshotCuentaExistenteException : Exception
{
    public SnapshotCuentaExistenteException()
        : base("Snapshot indica que ya existe una cuenta de N8N para este usuario.")
    {
    }
}
