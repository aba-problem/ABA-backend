/* =========================================================================
   ABA - Extensiones aditivas requeridas por backend-core (Entrega #2)
   Motor: Microsoft SQL Server (ABA_Control)

   Este script NO modifica ningún objeto de 001-006 (los scripts ya
   revisados/auditados de la célula) — solo AGREGA lo que el backend
   necesita y que no estaba cubierto:
     - Listado de bases del usuario para el dashboard (sin credenciales).
     - Actualización de espacio usado real + pausa/reactivación automática
       por cuota (Estado ya admite 'PAUSADA' en el CHECK de 001, se reutiliza
       ese valor en vez de inventar un estado nuevo).
     - Listado de bases MySQL activas, para el job de enforcement de cuota.
     - Listado de logins MySQL de un usuario, para sincronizar la tabla
       espejo aba_seguridad.whitelist_ip (pieza que 005_logon_trigger_mysql.sql
       deja explícitamente a cargo del backend).
     - Métricas públicas de la landing page (Entrega2.md), vía VIEW.
   ========================================================================= */

USE ABA_Control;
GO

/* -------------------------------------------------------------------------
   sp_ListarBasesDatosUsuario
   Dashboard (Módulo 3): info de conexión/estado SIN password. La password
   real solo la entrega sp_ObtenerCredencialesBaseDatos (002), en un
   endpoint separado y rate-limitado (control 3.2).
   ------------------------------------------------------------------------- */
CREATE OR ALTER PROCEDURE dbo.sp_ListarBasesDatosUsuario
    @UsuarioId INT
AS
BEGIN
    SET NOCOUNT ON;

    SELECT
        bd.Id, bd.NombreBD, bd.UsuarioBD, bd.Host, bd.Puerto,
        m.Nombre AS Motor, bd.Estado, bd.FechaCreacion, bd.UltimaActividad,
        bd.EspacioMaximoMB, bd.EspacioUtilizadoMB
    FROM dbo.BaseDeDatos bd
    INNER JOIN dbo.MotorBaseDatos m ON m.Id = bd.MotorId
    WHERE bd.UsuarioId = @UsuarioId
    ORDER BY bd.FechaCreacion DESC;
END
GO

/* -------------------------------------------------------------------------
   sp_ActualizarEspacioUsado
   El backend mide el uso REAL en el motor (information_schema.tables en
   MySQL) y llama esto para: (1) reflejarlo en ABA_Control, fuente de verdad
   del dashboard; (2) decidir AQUÍ (no en el backend) si debe pausar la BD
   por exceder EspacioMaximoMB, reutilizando el estado 'PAUSADA' ya definido
   en el CHECK de 001 (nunca se inventa un estado nuevo). Reactiva sola si
   el uso vuelve a bajar del límite.
   ------------------------------------------------------------------------- */
CREATE OR ALTER PROCEDURE dbo.sp_ActualizarEspacioUsado
    @BaseDeDatosId      INT,
    @EspacioUtilizadoMB DECIMAL(10,2)
AS
BEGIN
    SET NOCOUNT ON;
    SET XACT_ABORT ON;

    DECLARE @UsuarioId INT, @EstadoActual VARCHAR(20), @EspacioMaximoMB SMALLINT;

    SELECT @UsuarioId = UsuarioId, @EstadoActual = Estado, @EspacioMaximoMB = EspacioMaximoMB
    FROM dbo.BaseDeDatos
    WHERE Id = @BaseDeDatosId;

    IF @UsuarioId IS NULL
        RETURN; -- base inexistente (pudo eliminarse entre el escaneo y la actualización)

    DECLARE @ExcedeCuota BIT = CASE WHEN @EspacioUtilizadoMB > @EspacioMaximoMB THEN 1 ELSE 0 END;

    BEGIN TRY
        BEGIN TRAN;

        -- Solo transiciona ACTIVA<->PAUSADA por cuota; nunca toca PENDIENTE/ELIMINADA.
        UPDATE dbo.BaseDeDatos
        SET EspacioUtilizadoMB = @EspacioUtilizadoMB,
            UltimaActividad    = SYSUTCDATETIME(),
            Estado = CASE
                        WHEN @EstadoActual = 'ACTIVA'  AND @ExcedeCuota = 1 THEN 'PAUSADA'
                        WHEN @EstadoActual = 'PAUSADA' AND @ExcedeCuota = 0 THEN 'ACTIVA'
                        ELSE @EstadoActual
                     END
        WHERE Id = @BaseDeDatosId;

        IF @EstadoActual = 'ACTIVA' AND @ExcedeCuota = 1
            INSERT INTO dbo.Auditoria (UsuarioId, Entidad, EntidadId, Accion, Detalle)
            VALUES (@UsuarioId, 'BaseDeDatos', @BaseDeDatosId, 'PAUSAR_POR_CUOTA',
                    (SELECT @EspacioUtilizadoMB AS usadoMB, @EspacioMaximoMB AS maxMB FOR JSON PATH, WITHOUT_ARRAY_WRAPPER));

        IF @EstadoActual = 'PAUSADA' AND @ExcedeCuota = 0
            INSERT INTO dbo.Auditoria (UsuarioId, Entidad, EntidadId, Accion, Detalle)
            VALUES (@UsuarioId, 'BaseDeDatos', @BaseDeDatosId, 'REACTIVAR_POR_CUOTA',
                    (SELECT @EspacioUtilizadoMB AS usadoMB, @EspacioMaximoMB AS maxMB FOR JSON PATH, WITHOUT_ARRAY_WRAPPER));

        COMMIT TRAN;
    END TRY
    BEGIN CATCH
        IF XACT_STATE() <> 0 ROLLBACK TRAN;
        THROW;
    END CATCH
END
GO

/* -------------------------------------------------------------------------
   sp_ListarBasesActivasMySql
   Para el job de enforcement de cuota (Services/MySqlQuotaEnforcementService):
   solo bases del motor MySQL, únicas cuyo uso puede medirse desde el backend
   vía information_schema.tables. Las de motor SQLServer viven en la MISMA
   instancia que ABA_Control -- su medición (sp_spaceused por BD) queda como
   trabajo futuro, fuera del alcance de Entrega #2.
   ------------------------------------------------------------------------- */
CREATE OR ALTER PROCEDURE dbo.sp_ListarBasesActivasMySql
AS
BEGIN
    SET NOCOUNT ON;

    SELECT bd.Id, bd.UsuarioId, bd.NombreBD, bd.UsuarioBD, bd.EspacioMaximoMB, bd.EspacioUtilizadoMB, bd.Estado
    FROM dbo.BaseDeDatos bd
    INNER JOIN dbo.MotorBaseDatos m ON m.Id = bd.MotorId
    WHERE m.Nombre = 'MySQL' AND bd.Estado IN ('ACTIVA', 'PAUSADA');
END
GO

/* -------------------------------------------------------------------------
   sp_ListarUsuarioBDMySql
   005_logon_trigger_mysql.sql deja explícito que el backend debe replicar
   cambios de whitelist hacia aba_seguridad.whitelist_ip para los usuarios
   con BDs en MySQL. Esta consulta le da al backend la lista de logins MySQL
   (UsuarioBD) que debe sincronizar cuando cambia la whitelist de un usuario.
   ------------------------------------------------------------------------- */
CREATE OR ALTER PROCEDURE dbo.sp_ListarUsuarioBDMySql
    @UsuarioId INT
AS
BEGIN
    SET NOCOUNT ON;

    SELECT bd.UsuarioBD
    FROM dbo.BaseDeDatos bd
    INNER JOIN dbo.MotorBaseDatos m ON m.Id = bd.MotorId
    WHERE bd.UsuarioId = @UsuarioId AND m.Nombre = 'MySQL' AND bd.Estado = 'ACTIVA';
END
GO

/* -------------------------------------------------------------------------
   sp_ListarIpsActivasUsuario
   Complementa sp_ListarUsuarioBDMySql: el backend necesita el CONJUNTO
   completo de IPs activas del usuario (no solo la que se acaba de registrar)
   para poder reconciliar la tabla espejo aba_seguridad.whitelist_ip en MySQL
   -- desactivando ahí cualquier IP que ya no esté activa en ABA_Control.
   ------------------------------------------------------------------------- */
CREATE OR ALTER PROCEDURE dbo.sp_ListarIpsActivasUsuario
    @UsuarioId INT
AS
BEGIN
    SET NOCOUNT ON;

    SELECT DireccionIp
    FROM dbo.UsuarioIp
    WHERE UsuarioId = @UsuarioId AND Activo = 1;
END
GO

/* -------------------------------------------------------------------------
   Métricas públicas de la Landing Page (Entrega2.md) — control 4.1: solo
   agregados numéricos, nunca datos identificables de usuarios individuales.
   ------------------------------------------------------------------------- */
IF OBJECT_ID('dbo.EstadoServicio') IS NULL
BEGIN
    CREATE TABLE dbo.EstadoServicio
    (
        Id             INT IDENTITY(1,1) PRIMARY KEY,
        Disponibilidad DECIMAL(5,2) NOT NULL,
        ActualizadoEn  DATETIME2    NOT NULL DEFAULT SYSUTCDATETIME()
    );
END
GO
IF NOT EXISTS (SELECT 1 FROM dbo.EstadoServicio)
    INSERT INTO dbo.EstadoServicio (Disponibilidad) VALUES (99.90);
GO

CREATE OR ALTER VIEW dbo.vw_MetricasPublicas
AS
SELECT
    (SELECT COUNT(*) FROM dbo.Usuario WHERE Activo = 1)                          AS TotalUsuarios,
    (SELECT COUNT(*) FROM dbo.BaseDeDatos)                                       AS TotalBasesCreadas,
    (SELECT COUNT(*) FROM dbo.BaseDeDatos WHERE Estado = 'ACTIVA')               AS BasesActivas,
    (SELECT COUNT(*) FROM dbo.Auditoria WHERE Accion = 'LOGIN')                  AS TotalLogins,
    (SELECT COUNT(DISTINCT UsuarioId) FROM dbo.Auditoria
        WHERE Accion = 'LOGIN' AND FechaEvento >= DATEADD(DAY, -30, SYSUTCDATETIME())) AS UsuariosActivos30Dias,
    (SELECT TOP 1 Disponibilidad FROM dbo.EstadoServicio ORDER BY Id DESC)       AS DisponibilidadPorcentaje;
GO
