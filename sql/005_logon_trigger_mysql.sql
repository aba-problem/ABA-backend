/* =========================================================================
   ABA - Plataforma de Hosting DB & Servicios para Desarrolladores
   Equivalente a "logon trigger" para MySQL
   Motor: MySQL 8.0

   MySQL Community Edition NO tiene logon triggers nativos (eso es una
   característica propia de SQL Server). Tampoco puede hacer una consulta
   cruzada en vivo contra ABA_Control (son motores distintos, sin Linked
   Server posible). La alternativa estándar en MySQL es:

     1. Una tabla espejo local (aba_seguridad.whitelist_ip) con la misma
        información relevante de UsuarioIp/BaseDeDatos.
     2. Un procedimiento que valida la conexión, activado en cada conexión
        nueva vía la variable de sistema `init_connect`.
     3. `init_connect` NO se aplica a cuentas con privilegio SUPER /
        CONNECTION_ADMIN -- eso es justo lo que queremos: la cuenta admin
        nunca se bloquea, sin necesidad de un "IF usuario = root" frágil.

   PIEZA QUE FALTA Y LE TOCA AL BACKEND (no se puede resolver solo en SQL):
   cada vez que sp_RegistrarIpUsuario o sp_DesactivarUsuario cambien algo
   en ABA_Control para un usuario que tenga BDs en el motor MySQL, el
   backend debe replicar ese cambio aquí (upsert/desactivar en
   aba_seguridad.whitelist_ip). Sin ese paso, esta tabla queda desactualizada.
   ========================================================================= */

CREATE DATABASE IF NOT EXISTS aba_seguridad;

CREATE TABLE IF NOT EXISTS aba_seguridad.whitelist_ip (
    usuario_bd     VARCHAR(32)  NOT NULL,
    direccion_ip   VARCHAR(45)  NOT NULL,
    activo         TINYINT(1)   NOT NULL DEFAULT 1,
    actualizado_en DATETIME     NOT NULL DEFAULT CURRENT_TIMESTAMP ON UPDATE CURRENT_TIMESTAMP,
    PRIMARY KEY (usuario_bd, direccion_ip)
);

DELIMITER $$

CREATE PROCEDURE aba_seguridad.sp_validar_conexion()
BEGIN
    DECLARE v_usuario   VARCHAR(32);
    DECLARE v_ip        VARCHAR(45);
    DECLARE v_permitido INT DEFAULT 0;

    -- USER() devuelve 'nombre@host' tal como se conectó el cliente
    -- (a diferencia de CURRENT_USER(), que puede mostrar el host comodín
    -- '%' de la cuenta en vez de la IP real).
    SET v_usuario = SUBSTRING_INDEX(USER(), '@', 1);
    SET v_ip      = SUBSTRING_INDEX(USER(), '@', -1);

    SELECT COUNT(*) INTO v_permitido
    FROM aba_seguridad.whitelist_ip
    WHERE usuario_bd = v_usuario
      AND direccion_ip = v_ip
      AND activo = 1;

    IF v_permitido = 0 THEN
        SIGNAL SQLSTATE '45000'
            SET MESSAGE_TEXT = 'Conexion rechazada: IP no autorizada para este usuario.';
    END IF;
END$$

DELIMITER ;

/* -------------------------------------------------------------------------
   Activación (persistente): agrega esto al arranque del contenedor MySQL,
   NO lo corras solo con SET GLOBAL -- eso se pierde al reiniciar.
   En INFRAESTRUCTURA.md el contenedor se levanta con `docker run ... mysql:8.0
   --innodb-buffer-pool-size=256M --max-connections=50`; agrega el flag:

       --init-connect="CALL aba_seguridad.sp_validar_conexion()"

   Los usuarios con privilegio SUPER (root, y cualquier cuenta admin propia
   que crees) quedan exentos automáticamente -- no hace falta excluirlos
   a mano.
   ------------------------------------------------------------------------- */
