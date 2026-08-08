/* =========================================================================
   ABA - Plataforma de Hosting DB & Servicios para Desarrolladores
   Módulo 8 (continuación): rotación de contraseña para bases de células socias.

   Motivo: el passwordTemporal de sp_AprovisionarBaseDatosSocio se entrega UNA SOLA
   VEZ (Aba/API-CELULAS-SOCIAS.md § 5.1). Sin esta SP, la única forma de recuperar el
   acceso tras perder/filtrar esa contraseña era borrar la base entera y recrearla
   (pérdida de datos real). Esta SP regenera y re-cifra sin tocar NombreBD/UsuarioBD
   ni el contenido de la base.
   ========================================================================= */

USE ABA_Control;
GO

-- Mismo mecanismo de generación/cifrado que sp_AprovisionarBaseDatosSocio, copiado
-- deliberadamente (no compartido): CRYPT_GEN_RANDOM no se puede envolver en una
-- función escalar, ver comentario de cabecera de sp_AprovisionarBaseDatosSocio en
-- 013_celulas_socias.sql.
CREATE OR ALTER PROCEDURE dbo.sp_RotarPasswordBaseDatosSocio
    @BaseDeDatosSocioId INT,
    @CelulaSociaId       INT
AS
BEGIN
    SET NOCOUNT ON;
    SET XACT_ABORT ON;

    -- Solo se rota la contraseña de una base ACTIVA: 'PENDIENTE' todavía no existe en
    -- MySQL (nada que rotar) y 'ELIMINADA' ya no existe (nada que aplicar del otro lado).
    DECLARE @NombreBD VARCHAR(63), @UsuarioBD VARCHAR(32), @Host VARCHAR(255), @Puerto INT;
    SELECT @NombreBD = NombreBD, @UsuarioBD = UsuarioBD, @Host = Host, @Puerto = Puerto
    FROM dbo.BaseDeDatosSocio
    WHERE Id = @BaseDeDatosSocioId AND CelulaSociaId = @CelulaSociaId AND Estado = 'ACTIVA';

    IF @UsuarioBD IS NULL
        THROW 50106, 'La base de datos no existe, no pertenece a esta celula, o no esta activa.', 1;

    DECLARE @Longitud INT = 20;
    DECLARE @Charset VARCHAR(94) = 'ABCDEFGHJKLMNPQRSTUVWXYZabcdefghijkmnpqrstuvwxyz23456789!@#$%^&*-_=+';
    DECLARE @RandomBytes VARBINARY(100) = CRYPT_GEN_RANDOM(@Longitud);
    DECLARE @PasswordPlano VARCHAR(50) = '';
    DECLARE @i INT = 1, @Byte INT;

    WHILE @i <= @Longitud
    BEGIN
        SET @Byte = CAST(SUBSTRING(@RandomBytes, @i, 1) AS INT);
        SET @PasswordPlano = @PasswordPlano + SUBSTRING(@Charset, (@Byte % LEN(@Charset)) + 1, 1);
        SET @i += 1;
    END

    SET @PasswordPlano = STUFF(@PasswordPlano, 1, 1, SUBSTRING('ABCDEFGHJKLMNPQRSTUVWXYZ', (ABS(CHECKSUM(NEWID())) % 24) + 1, 1));
    SET @PasswordPlano = STUFF(@PasswordPlano, 2, 1, SUBSTRING('abcdefghijkmnpqrstuvwxyz', (ABS(CHECKSUM(NEWID())) % 24) + 1, 1));
    SET @PasswordPlano = STUFF(@PasswordPlano, 3, 1, SUBSTRING('23456789', (ABS(CHECKSUM(NEWID())) % 8) + 1, 1));
    SET @PasswordPlano = STUFF(@PasswordPlano, 4, 1, SUBSTRING('!@#$%^&*-_=+', (ABS(CHECKSUM(NEWID())) % 12) + 1, 1));

    DECLARE @PasswordCifrado VARBINARY(256);
    OPEN SYMMETRIC KEY SymKey_ABA_Credenciales DECRYPTION BY CERTIFICATE Cert_ABA_Credenciales;
    SET @PasswordCifrado = ENCRYPTBYKEY(KEY_GUID('SymKey_ABA_Credenciales'), @PasswordPlano);
    CLOSE SYMMETRIC KEY SymKey_ABA_Credenciales;

    BEGIN TRY
        BEGIN TRAN;

        -- ABA_Control queda como fuente de verdad ANTES de que el backend toque MySQL.
        -- Si el paso de MySQL falla después, esta fila sigue reflejando la contraseña
        -- nueva (no la que realmente sigue activa en el motor) hasta el próximo intento
        -- de reset — no hay "abandonar la fila" como en el aprovisionamiento inicial
        -- porque la base ya existe y sigue funcionando con la contraseña vieja mientras
        -- tanto. El backend NO reintenta automáticamente; el llamador simplemente vuelve
        -- a pedir el reset (ver PartnersProvisioningOrchestrator.RotarPasswordAsync).
        UPDATE dbo.BaseDeDatosSocio
        SET PasswordCifrado = @PasswordCifrado, UltimaActividad = SYSUTCDATETIME()
        WHERE Id = @BaseDeDatosSocioId;

        INSERT INTO dbo.Auditoria (UsuarioId, Entidad, EntidadId, Accion)
        VALUES (NULL, 'BaseDeDatosSocio', @BaseDeDatosSocioId, 'RESET_PASSWORD');

        COMMIT TRAN;
    END TRY
    BEGIN CATCH
        IF XACT_STATE() <> 0 ROLLBACK TRAN;
        THROW;
    END CATCH

    SELECT
        @BaseDeDatosSocioId AS BaseDeDatosId,
        @NombreBD           AS NombreBD,
        @UsuarioBD          AS UsuarioBD,
        @PasswordPlano      AS PasswordNueva,
        @Host               AS Host,
        @Puerto             AS Puerto;
END
GO
