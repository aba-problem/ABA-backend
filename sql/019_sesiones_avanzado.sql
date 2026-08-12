/* =========================================================================
   ABA - Registros de sesión: soporte real de backend (Entregable — rediseño
   de UX de "Registros de sesión"). Script aditivo — no modifica 001-018.

   Tres piezas que la vista rediseñada necesita y que no existían:
     1) Paginación real en sp_ListarSesionesUsuario (antes TOP 50 fijo).
     2) sp_RevocarIpUsuario — el botón "No fui yo, bloquear" del frontend
        necesita un camino real para desactivar una IP puntual.
     3) Ciudad (geo-IP) y User-Agent en el detalle de los eventos, para que
        la vista pueda mostrar algo más que la IP cruda — sin inventar nada:
        si el proveedor geo-IP no resuelve ciudad, o el navegador no manda
        User-Agent, el campo simplemente queda NULL y el frontend lo omite.

   CREATE OR ALTER sobre sp_CrearUsuario/sp_RegistrarIpUsuario (originales en
   002/003, ya extendido una vez en 011) — mismo patrón aditivo de siempre,
   nunca se edita el archivo original.
   ========================================================================= */

USE ABA_Control;
GO

/* -------------------------------------------------------------------------
   sp_CrearUsuario — suma @UserAgent (informativo, solo para Registros de
   sesión). Cuerpo idéntico a la versión de 011 salvo ese parámetro y su
   inclusión en el Detalle JSON del evento LOGIN/REGISTRO.
   ------------------------------------------------------------------------- */
CREATE OR ALTER PROCEDURE dbo.sp_CrearUsuario
    @Nombre             NVARCHAR(150),
    @Correo             NVARCHAR(255),
    @AvatarUrl          NVARCHAR(500) = NULL,
    @Proveedor          VARCHAR(20),
    @ProveedorUsuarioId VARCHAR(100),
    @IpOrigen           VARCHAR(45)   = NULL,
    @UserAgent          NVARCHAR(500) = NULL
AS
BEGIN
    SET NOCOUNT ON;
    SET XACT_ABORT ON;

    IF @Proveedor NOT IN ('GOOGLE', 'GITHUB')
        THROW 50001, 'Proveedor de autenticación no soportado.', 1;

    DECLARE @UsuarioId INT;
    DECLARE @EsNuevo BIT = 0;

    BEGIN TRY
        BEGIN TRAN;

        SELECT @UsuarioId = Id
        FROM dbo.Usuario WITH (UPDLOCK, HOLDLOCK)
        WHERE Proveedor = @Proveedor AND ProveedorUsuarioId = @ProveedorUsuarioId;

        IF @UsuarioId IS NULL
        BEGIN
            INSERT INTO dbo.Usuario (Nombre, Correo, AvatarUrl, Proveedor, ProveedorUsuarioId, UltimoLogin)
            VALUES (@Nombre, @Correo, @AvatarUrl, @Proveedor, @ProveedorUsuarioId, SYSUTCDATETIME());

            SET @UsuarioId = SCOPE_IDENTITY();
            SET @EsNuevo = 1;
        END
        ELSE
        BEGIN
            IF EXISTS (SELECT 1 FROM dbo.Usuario WHERE Id = @UsuarioId AND Activo = 0)
                THROW 50005, 'Esta cuenta fue desactivada. Contacta al administrador.', 1;

            UPDATE dbo.Usuario
            SET Nombre      = @Nombre,
                Correo      = @Correo,
                AvatarUrl   = @AvatarUrl,
                UltimoLogin = SYSUTCDATETIME()
            WHERE Id = @UsuarioId;
        END

        INSERT INTO dbo.Auditoria (UsuarioId, Entidad, EntidadId, Accion, IpOrigen, Detalle)
        VALUES (
            @UsuarioId, 'Usuario', @UsuarioId,
            CASE WHEN @EsNuevo = 1 THEN 'REGISTRO' ELSE 'LOGIN' END,
            @IpOrigen,
            (SELECT @Proveedor AS proveedor, @UserAgent AS userAgent FOR JSON PATH, WITHOUT_ARRAY_WRAPPER)
        );

        COMMIT TRAN;
    END TRY
    BEGIN CATCH
        IF XACT_STATE() <> 0 ROLLBACK TRAN;
        THROW;
    END CATCH

    SELECT Id, Nombre, Correo, AvatarUrl, Proveedor, FechaCreacion, UltimoLogin, EsAdmin
    FROM dbo.Usuario
    WHERE Id = @UsuarioId;
END
GO

/* -------------------------------------------------------------------------
   sp_RegistrarIpUsuario — suma @Ciudad (informativo). Cuerpo idéntico al
   original de 003 salvo ese parámetro y su inclusión en el Detalle JSON de
   IP_VALIDADA e IP_RECHAZADA.
   ------------------------------------------------------------------------- */
CREATE OR ALTER PROCEDURE dbo.sp_RegistrarIpUsuario
    @UsuarioId   INT,
    @DireccionIp VARCHAR(45),
    @PaisIso     CHAR(2),
    @Ciudad      NVARCHAR(100) = NULL
AS
BEGIN
    SET NOCOUNT ON;
    SET XACT_ABORT ON;

    IF NOT EXISTS (SELECT 1 FROM dbo.Usuario WHERE Id = @UsuarioId AND Activo = 1)
        THROW 50009, 'Usuario no existe o está inactivo.', 1;

    IF NOT EXISTS (SELECT 1 FROM dbo.PaisPermitido WHERE PaisIso = @PaisIso AND Activo = 1)
    BEGIN
        INSERT INTO dbo.Auditoria (UsuarioId, Entidad, EntidadId, Accion, IpOrigen, Detalle)
        VALUES (@UsuarioId, 'UsuarioIp', NULL, 'IP_RECHAZADA', @DireccionIp,
                (SELECT @PaisIso AS paisIso, @Ciudad AS ciudad FOR JSON PATH, WITHOUT_ARRAY_WRAPPER));

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

        INSERT INTO dbo.Auditoria (UsuarioId, Entidad, EntidadId, Accion, IpOrigen, Detalle)
        VALUES (@UsuarioId, 'UsuarioIp', @UsuarioIpId, 'IP_VALIDADA', @DireccionIp,
                (SELECT @Ciudad AS ciudad FOR JSON PATH, WITHOUT_ARRAY_WRAPPER));

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

/* -------------------------------------------------------------------------
   sp_ListarSesionesUsuario — ahora con paginación real (antes TOP 50 fijo).
   TotalRegistros va repetido en cada fila (COUNT(*) OVER()) para que el
   backend arme la respuesta paginada en una sola llamada, sin round-trip
   aparte. @TamanoPagina tope en 100 — evita que un valor absurdo del
   cliente fuerce un table scan gigante de Auditoria.
   ------------------------------------------------------------------------- */
CREATE OR ALTER PROCEDURE dbo.sp_ListarSesionesUsuario
    @UsuarioId     INT,
    @Pagina        INT = 1,
    @TamanoPagina  INT = 20
AS
BEGIN
    SET NOCOUNT ON;

    IF @Pagina < 1 SET @Pagina = 1;
    IF @TamanoPagina < 1 SET @TamanoPagina = 20;
    IF @TamanoPagina > 100 SET @TamanoPagina = 100;

    SELECT
        Id, Entidad, Accion, IpOrigen, FechaEvento, Detalle,
        COUNT(*) OVER() AS TotalRegistros
    FROM dbo.Auditoria
    WHERE UsuarioId = @UsuarioId
      AND Accion IN ('LOGIN', 'REGISTRO', 'IP_VALIDADA', 'IP_RECHAZADA', 'IP_REVOCADA')
    ORDER BY FechaEvento DESC
    OFFSET (@Pagina - 1) * @TamanoPagina ROWS
    FETCH NEXT @TamanoPagina ROWS ONLY;
END
GO

/* -------------------------------------------------------------------------
   sp_RevocarIpUsuario — "No fui yo, bloquear" desde Registros de sesión.
   Desactiva esa IP puntual en UsuarioIp (el backend, después de este SP,
   llama a MySqlWhitelistSyncService para que el espejo en MySQL refleje el
   cambio de inmediato — ver SesionesController.cs). BOLA: @UsuarioId
   siempre del token; @DireccionIp debe pertenecerle o el SP rechaza (no
   revela si la IP existe para OTRO usuario — mismo 50011 en ambos casos).
   ------------------------------------------------------------------------- */
CREATE OR ALTER PROCEDURE dbo.sp_RevocarIpUsuario
    @UsuarioId         INT,
    @DireccionIp       VARCHAR(45),
    @IpOrigenSolicitud VARCHAR(45) = NULL
AS
BEGIN
    SET NOCOUNT ON;
    SET XACT_ABORT ON;

    IF NOT EXISTS (
        SELECT 1 FROM dbo.UsuarioIp
        WHERE UsuarioId = @UsuarioId AND DireccionIp = @DireccionIp AND Activo = 1
    )
        THROW 50011, 'Esa IP no existe o ya no está activa para tu cuenta.', 1;

    BEGIN TRY
        BEGIN TRAN;

        UPDATE dbo.UsuarioIp
        SET Activo = 0
        WHERE UsuarioId = @UsuarioId AND DireccionIp = @DireccionIp;

        INSERT INTO dbo.Auditoria (UsuarioId, Entidad, EntidadId, Accion, IpOrigen, Detalle)
        VALUES (@UsuarioId, 'UsuarioIp', NULL, 'IP_REVOCADA', @IpOrigenSolicitud,
                (SELECT @DireccionIp AS ipRevocada FOR JSON PATH, WITHOUT_ARRAY_WRAPPER));

        COMMIT TRAN;
    END TRY
    BEGIN CATCH
        IF XACT_STATE() <> 0 ROLLBACK TRAN;
        THROW;
    END CATCH
END
GO
