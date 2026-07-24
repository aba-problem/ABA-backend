USE ABA_Control;
GO

CREATE OR ALTER PROCEDURE dbo.sp_ObtenerPerfilUsuario
    @UsuarioId INT
AS
BEGIN
    SET NOCOUNT ON;

    SELECT
        Id          AS UsuarioId,
        Nombre,
        Correo,
        AvatarUrl,
        Proveedor,
        FechaCreacion,
        UltimoLogin
    FROM dbo.Usuario
    WHERE Id = @UsuarioId AND Activo = 1;
END
GO
