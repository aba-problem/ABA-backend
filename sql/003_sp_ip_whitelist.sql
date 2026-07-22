/* =========================================================================
   ABA - Plataforma de Hosting DB & Servicios para Desarrolladores
   Stored Procedure: whitelist automática de IPs (UsuarioIp)
   Motor: Microsoft SQL Server (ABA_Control)

   El usuario final nunca gestiona esto a mano. El backend llama a este SP
   en cada login (justo después de sp_CrearUsuario), pasando la IP real de
   la petición y el país resuelto por geo-IP. La regla "solo América/Latam"
   ya está garantizada por la FK UsuarioIp -> PaisPermitido (ver 001); este
   SP además controla que la whitelist no crezca sin límite y deja rastro
   de intentos rechazados para poder detectar patrones sospechosos.
   ========================================================================= */

USE ABA_Control;
GO

/* -------------------------------------------------------------------------
   sp_RegistrarIpUsuario
   Upsert de UsuarioIp con Origen='AUTO'. Si el país no está permitido,
   audita el intento como 'IP_RECHAZADA' (sin crear la fila) y lanza error
   -- así el Logon Trigger del motor solo verá IPs que sí pasaron el filtro.
   Mantiene como máximo 5 IPs activas por usuario (las más recientes);
   las IPs dinámicas de casa/oficina van rotando y esto evita que la
   whitelist crezca indefinidamente (importa para el rendimiento del
   Logon Trigger, que consulta esta tabla en cada intento de conexión).
   ------------------------------------------------------------------------- */
CREATE OR ALTER PROCEDURE dbo.sp_RegistrarIpUsuario
    @UsuarioId   INT,
    @DireccionIp VARCHAR(45),
    @PaisIso     CHAR(2)
AS
BEGIN
    SET NOCOUNT ON;
    SET XACT_ABORT ON;

    IF NOT EXISTS (SELECT 1 FROM dbo.Usuario WHERE Id = @UsuarioId AND Activo = 1)
        THROW 50009, 'Usuario no existe o está inactivo.', 1;

    -- Región no permitida: se audita el intento antes de rechazar, para
    -- poder detectar patrones de acceso sospechosos fuera de América/Latam.
    IF NOT EXISTS (SELECT 1 FROM dbo.PaisPermitido WHERE PaisIso = @PaisIso AND Activo = 1)
    BEGIN
        INSERT INTO dbo.Auditoria (UsuarioId, Entidad, EntidadId, Accion, IpOrigen, Detalle)
        VALUES (@UsuarioId, 'UsuarioIp', NULL, 'IP_RECHAZADA', @DireccionIp,
                (SELECT @PaisIso AS paisIso FOR JSON PATH, WITHOUT_ARRAY_WRAPPER));

        THROW 50010, 'La IP no pertenece a una región permitida (América/Latam).', 1;
    END

    DECLARE @UsuarioIpId INT;

    BEGIN TRY
        BEGIN TRAN;

        SELECT @UsuarioIpId = Id
        FROM dbo.UsuarioIp WITH (UPDLOCK, HOLDLOCK)
        WHERE UsuarioId = @UsuarioId AND DireccionIp = @DireccionIp;

        IF @UsuarioIpId IS NULL
        BEGIN
            INSERT INTO dbo.UsuarioIp (UsuarioId, DireccionIp, PaisIso, Origen, Activo, FechaVerificacion)
            VALUES (@UsuarioId, @DireccionIp, @PaisIso, 'AUTO', 1, SYSUTCDATETIME());

            SET @UsuarioIpId = SCOPE_IDENTITY();
        END
        ELSE
        BEGIN
            UPDATE dbo.UsuarioIp
            SET PaisIso           = @PaisIso,
                Activo            = 1,
                FechaVerificacion = SYSUTCDATETIME()
            WHERE Id = @UsuarioIpId;
        END

        -- Housekeeping: deja activas solo las 5 IPs más recientes del usuario
        ;WITH IpsActivas AS (
            SELECT Id, ROW_NUMBER() OVER (ORDER BY FechaVerificacion DESC) AS Orden
            FROM dbo.UsuarioIp
            WHERE UsuarioId = @UsuarioId AND Activo = 1
        )
        UPDATE ui
        SET Activo = 0
        FROM dbo.UsuarioIp ui
        INNER JOIN IpsActivas ia ON ia.Id = ui.Id
        WHERE ia.Orden > 5;

        INSERT INTO dbo.Auditoria (UsuarioId, Entidad, EntidadId, Accion, IpOrigen)
        VALUES (@UsuarioId, 'UsuarioIp', @UsuarioIpId, 'IP_VALIDADA', @DireccionIp);

        COMMIT TRAN;
    END TRY
    BEGIN CATCH
        IF XACT_STATE() <> 0 ROLLBACK TRAN;
        THROW;
    END CATCH

    SELECT Id, DireccionIp, PaisIso, Activo, FechaVerificacion
    FROM dbo.UsuarioIp
    WHERE Id = @UsuarioIpId;
END
GO
