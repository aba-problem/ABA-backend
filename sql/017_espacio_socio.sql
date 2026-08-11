/* =========================================================================
   ABA - Plataforma de Hosting DB & Servicios para Desarrolladores
   Módulo 8 (continuación): medición y enforcement de cuota de espacio para
   bases de células socias.

   Motivo: al crear dbo.BaseDeDatosSocio (013_celulas_socias.sql, líneas 89-104)
   quedó documentado como "costo aceptado" que MySqlQuotaEnforcementService no
   cubriría estas filas "mientras no haya bases de células socias todavía".
   Con raft (y otras) ya aprovisionadas y escribiendo datos reales, ese costo
   dejó de ser aceptable: EspacioUtilizadoMB nunca se actualizaba (quedaba en
   el DEFAULT 0 para siempre) y no había forma de bloquear una base que se
   pasara de EspacioMaximoMB. Esta migración extiende el MISMO mecanismo que
   ya corre para estudiantes (sp_ActualizarEspacioUsado / sp_ListarBasesActivasMySql,
   007_extensiones_backend.sql) a BaseDeDatosSocio, sin tocar ninguno de los 6 SPs
   de dbo.BaseDeDatos que motivaron separar la tabla en primer lugar.
   ========================================================================= */

USE ABA_Control;
GO

-- 'PAUSADA' ya es un estado válido para dbo.BaseDeDatos (001_init_control_db.sql) —
-- se reusa el mismo nombre acá, nunca se inventa un estado nuevo para lo mismo.
ALTER TABLE dbo.BaseDeDatosSocio DROP CONSTRAINT CK_BaseDeDatosSocio_Estado;
GO
ALTER TABLE dbo.BaseDeDatosSocio ADD CONSTRAINT CK_BaseDeDatosSocio_Estado
    CHECK (Estado IN ('PENDIENTE', 'ACTIVA', 'PAUSADA', 'ELIMINADA'));
GO

/* -------------------------------------------------------------------------
   sp_ListarBasesActivasMySqlSocio
   Espejo de sp_ListarBasesActivasMySql, para que MySqlQuotaEnforcementService
   sepa qué filas de BaseDeDatosSocio escanear. No hace falta filtrar por motor
   (a diferencia de la versión de estudiantes): sp_AprovisionarBaseDatosSocio ya
   fuerza MySQL como único motor posible para células socias.
   ------------------------------------------------------------------------- */
CREATE OR ALTER PROCEDURE dbo.sp_ListarBasesActivasMySqlSocio
AS
BEGIN
    SET NOCOUNT ON;

    SELECT Id, CelulaSociaId, NombreBD, UsuarioBD, EspacioMaximoMB, EspacioUtilizadoMB, Estado
    FROM dbo.BaseDeDatosSocio
    WHERE Estado IN ('ACTIVA', 'PAUSADA');
END
GO

/* -------------------------------------------------------------------------
   sp_ActualizarEspacioUsadoSocio
   Espejo exacto de sp_ActualizarEspacioUsado: decide en SQL (no en el backend)
   si pausa o reactiva la base según EspacioMaximoMB. Auditado bajo
   Entidad='BaseDeDatosSocio' (ya habilitado en CK_Auditoria_Entidad desde 013).
   ------------------------------------------------------------------------- */
CREATE OR ALTER PROCEDURE dbo.sp_ActualizarEspacioUsadoSocio
    @BaseDeDatosSocioId INT,
    @EspacioUtilizadoMB DECIMAL(10,2)
AS
BEGIN
    SET NOCOUNT ON;
    SET XACT_ABORT ON;

    DECLARE @CelulaSociaId INT, @EstadoActual VARCHAR(20), @EspacioMaximoMB SMALLINT;

    SELECT @CelulaSociaId = CelulaSociaId, @EstadoActual = Estado, @EspacioMaximoMB = EspacioMaximoMB
    FROM dbo.BaseDeDatosSocio
    WHERE Id = @BaseDeDatosSocioId;

    IF @CelulaSociaId IS NULL
        RETURN; -- base inexistente (pudo eliminarse entre el escaneo y la actualización)

    DECLARE @ExcedeCuota BIT = CASE WHEN @EspacioUtilizadoMB > @EspacioMaximoMB THEN 1 ELSE 0 END;

    BEGIN TRY
        BEGIN TRAN;

        -- Solo transiciona ACTIVA<->PAUSADA por cuota; nunca toca PENDIENTE/ELIMINADA.
        UPDATE dbo.BaseDeDatosSocio
        SET EspacioUtilizadoMB = @EspacioUtilizadoMB,
            UltimaActividad    = SYSUTCDATETIME(),
            Estado = CASE
                        WHEN @EstadoActual = 'ACTIVA'  AND @ExcedeCuota = 1 THEN 'PAUSADA'
                        WHEN @EstadoActual = 'PAUSADA' AND @ExcedeCuota = 0 THEN 'ACTIVA'
                        ELSE @EstadoActual
                     END
        WHERE Id = @BaseDeDatosSocioId;

        IF @EstadoActual = 'ACTIVA' AND @ExcedeCuota = 1
            INSERT INTO dbo.Auditoria (UsuarioId, Entidad, EntidadId, Accion, Detalle)
            VALUES (NULL, 'BaseDeDatosSocio', @BaseDeDatosSocioId, 'PAUSAR_POR_CUOTA',
                    (SELECT @CelulaSociaId AS celulaSociaId, @EspacioUtilizadoMB AS usadoMB, @EspacioMaximoMB AS maxMB
                     FOR JSON PATH, WITHOUT_ARRAY_WRAPPER));

        IF @EstadoActual = 'PAUSADA' AND @ExcedeCuota = 0
            INSERT INTO dbo.Auditoria (UsuarioId, Entidad, EntidadId, Accion, Detalle)
            VALUES (NULL, 'BaseDeDatosSocio', @BaseDeDatosSocioId, 'REACTIVAR_POR_CUOTA',
                    (SELECT @CelulaSociaId AS celulaSociaId, @EspacioUtilizadoMB AS usadoMB, @EspacioMaximoMB AS maxMB
                     FOR JSON PATH, WITHOUT_ARRAY_WRAPPER));

        COMMIT TRAN;
    END TRY
    BEGIN CATCH
        IF XACT_STATE() <> 0 ROLLBACK TRAN;
        THROW;
    END CATCH
END
GO

/* -------------------------------------------------------------------------
   PorcentajeUsado agregado a los dos SPs de lectura que ya expone la API de
   partners (GET /partners/databases y GET /partners/databases/{id}), para que
   la célula no tenga que hardcodear el límite de 20MB de su lado ni hacer la
   cuenta ella misma — si el límite cambia algún día, el número sigue siendo
   correcto sin que tengan que tocar nada.
   ------------------------------------------------------------------------- */
CREATE OR ALTER PROCEDURE dbo.sp_ObtenerBaseDatosSocioPorId
    @BaseDeDatosSocioId INT,
    @CelulaSociaId       INT
AS
BEGIN
    SET NOCOUNT ON;

    SELECT Id, NombreBD, UsuarioBD, Host, Puerto, Estado,
           EspacioMaximoMB, EspacioUtilizadoMB,
           CAST(ROUND(CASE WHEN EspacioMaximoMB = 0 THEN 0
                            ELSE EspacioUtilizadoMB * 100.0 / EspacioMaximoMB END, 0) AS INT) AS PorcentajeUsado,
           FechaCreacion
    FROM dbo.BaseDeDatosSocio
    WHERE Id = @BaseDeDatosSocioId AND CelulaSociaId = @CelulaSociaId;
END
GO

CREATE OR ALTER PROCEDURE dbo.sp_ListarBasesDatosSocio
    @CelulaSociaId INT
AS
BEGIN
    SET NOCOUNT ON;

    SELECT Id, NombreBD, UsuarioBD, Host, Puerto, Estado,
           EspacioMaximoMB, EspacioUtilizadoMB,
           CAST(ROUND(CASE WHEN EspacioMaximoMB = 0 THEN 0
                            ELSE EspacioUtilizadoMB * 100.0 / EspacioMaximoMB END, 0) AS INT) AS PorcentajeUsado,
           FechaCreacion
    FROM dbo.BaseDeDatosSocio
    WHERE CelulaSociaId = @CelulaSociaId
    ORDER BY FechaCreacion DESC;
END
GO
