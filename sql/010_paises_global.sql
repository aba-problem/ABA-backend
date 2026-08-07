/* =========================================================================
   ABA - Plataforma de Hosting DB & Servicios para Desarrolladores
   Amplía dbo.PaisPermitido (sql/001) de "solo América/Latam" a acceso mundial.

   Decisión 2026-08-06: con la API de células socias (sql/009) abriéndose a
   nivel mundial, la restricción geográfica original para el login/whitelist
   de IP de nuestros propios estudiantes deja de tener sentido — cualquiera,
   desde cualquier país, debe poder usar la plataforma con cualquier gestor
   de bases de datos.

   Deliberadamente NO se toca ningún SP ni el mecanismo de sp_RegistrarIpUsuario
   (sql/003): la restricción se implementa 100% vía el catálogo (FK), tal como
   el comentario original de 001_init_control_db.sql ya anticipaba
   ("Ajusta la lista según la política final"). Solo se agregan los países
   ISO-3166-1 alpha-2 que faltan — WHERE NOT EXISTS hace el script re-ejecutable
   sin duplicar los 29 que ya estaban cargados.
   ========================================================================= */

USE ABA_Control;
GO

INSERT INTO dbo.PaisPermitido (PaisIso, Nombre)
SELECT v.PaisIso, v.Nombre
FROM (VALUES
    ('AD','Andorra'),('AL','Albania'),('AT','Austria'),('BA','Bosnia y Herzegovina'),
    ('BE','Belgica'),('BG','Bulgaria'),('BY','Bielorrusia'),('CH','Suiza'),
    ('CY','Chipre'),('CZ','Chequia'),('DE','Alemania'),('DK','Dinamarca'),
    ('EE','Estonia'),('ES','Espana'),('FI','Finlandia'),('FO','Islas Feroe'),
    ('FR','Francia'),('GB','Reino Unido'),('GI','Gibraltar'),('GR','Grecia'),
    ('HR','Croacia'),('HU','Hungria'),('IE','Irlanda'),('IS','Islandia'),
    ('IT','Italia'),('LI','Liechtenstein'),('LT','Lituania'),('LU','Luxemburgo'),
    ('LV','Letonia'),('MC','Monaco'),('MD','Moldavia'),('ME','Montenegro'),
    ('MK','Macedonia del Norte'),('MT','Malta'),('NL','Paises Bajos'),('NO','Noruega'),
    ('PL','Polonia'),('PT','Portugal'),('RO','Rumania'),('RS','Serbia'),
    ('RU','Rusia'),('SE','Suecia'),('SI','Eslovenia'),('SK','Eslovaquia'),
    ('SM','San Marino'),('UA','Ucrania'),('VA','Ciudad del Vaticano'),('AX','Aland'),
    ('GG','Guernsey'),('IM','Isla de Man'),('JE','Jersey')
) AS v(PaisIso, Nombre)
WHERE NOT EXISTS (SELECT 1 FROM dbo.PaisPermitido p WHERE p.PaisIso = v.PaisIso);
GO

INSERT INTO dbo.PaisPermitido (PaisIso, Nombre)
SELECT v.PaisIso, v.Nombre
FROM (VALUES
    ('AE','Emiratos Arabes Unidos'),('AF','Afganistan'),('AM','Armenia'),('AZ','Azerbaiyan'),
    ('BD','Bangladesh'),('BH','Bahrein'),('BN','Brunei'),('BT','Butan'),
    ('CN','China'),('GE','Georgia'),('HK','Hong Kong'),('ID','Indonesia'),
    ('IL','Israel'),('IN','India'),('IQ','Irak'),('IR','Iran'),
    ('JO','Jordania'),('JP','Japon'),('KG','Kirguistan'),('KH','Camboya'),
    ('KP','Corea del Norte'),('KR','Corea del Sur'),('KW','Kuwait'),('KZ','Kazajistan'),
    ('LA','Laos'),('LB','Libano'),('LK','Sri Lanka'),('MM','Myanmar'),
    ('MN','Mongolia'),('MO','Macao'),('MV','Maldivas'),('MY','Malasia'),
    ('NP','Nepal'),('OM','Oman'),('PH','Filipinas'),('PK','Pakistan'),
    ('PS','Palestina'),('QA','Catar'),('SA','Arabia Saudita'),('SG','Singapur'),
    ('SY','Siria'),('TH','Tailandia'),('TJ','Tayikistan'),('TL','Timor Oriental'),
    ('TM','Turkmenistan'),('TR','Turquia'),('TW','Taiwan'),('UZ','Uzbekistan'),
    ('VN','Vietnam'),('YE','Yemen')
) AS v(PaisIso, Nombre)
WHERE NOT EXISTS (SELECT 1 FROM dbo.PaisPermitido p WHERE p.PaisIso = v.PaisIso);
GO

INSERT INTO dbo.PaisPermitido (PaisIso, Nombre)
SELECT v.PaisIso, v.Nombre
FROM (VALUES
    ('AO','Angola'),('BF','Burkina Faso'),('BI','Burundi'),('BJ','Benin'),
    ('BW','Botsuana'),('CD','Rep Dem del Congo'),('CF','Rep Centroafricana'),('CG','Congo'),
    ('CI','Costa de Marfil'),('CM','Camerun'),('CV','Cabo Verde'),('DJ','Yibuti'),
    ('DZ','Argelia'),('EG','Egipto'),('EH','Sahara Occidental'),('ER','Eritrea'),
    ('ET','Etiopia'),('GA','Gabon'),('GH','Ghana'),('GM','Gambia'),
    ('GN','Guinea'),('GQ','Guinea Ecuatorial'),('GW','Guinea-Bisau'),('KE','Kenia'),
    ('KM','Comoras'),('LR','Liberia'),('LS','Lesoto'),('LY','Libia'),
    ('MA','Marruecos'),('MG','Madagascar'),('ML','Mali'),('MR','Mauritania'),
    ('MU','Mauricio'),('MW','Malaui'),('MZ','Mozambique'),('NA','Namibia'),
    ('NE','Niger'),('NG','Nigeria'),('RW','Ruanda'),('SC','Seychelles'),
    ('SD','Sudan'),('SL','Sierra Leona'),('SN','Senegal'),('SO','Somalia'),
    ('SS','Sudan del Sur'),('ST','Santo Tome y Principe'),('SZ','Esuatini'),('TD','Chad'),
    ('TG','Togo'),('TN','Tunez'),('TZ','Tanzania'),('UG','Uganda'),
    ('ZA','Sudafrica'),('ZM','Zambia'),('ZW','Zimbabue')
) AS v(PaisIso, Nombre)
WHERE NOT EXISTS (SELECT 1 FROM dbo.PaisPermitido p WHERE p.PaisIso = v.PaisIso);
GO

INSERT INTO dbo.PaisPermitido (PaisIso, Nombre)
SELECT v.PaisIso, v.Nombre
FROM (VALUES
    ('AS','Samoa Americana'),('AU','Australia'),('CK','Islas Cook'),('FJ','Fiyi'),
    ('FM','Micronesia'),('GU','Guam'),('KI','Kiribati'),('MH','Islas Marshall'),
    ('MP','Islas Marianas del Norte'),('NC','Nueva Caledonia'),('NF','Isla Norfolk'),('NR','Nauru'),
    ('NU','Niue'),('NZ','Nueva Zelanda'),('PF','Polinesia Francesa'),('PG','Papua Nueva Guinea'),
    ('PW','Palaos'),('SB','Islas Salomon'),('TK','Tokelau'),('TO','Tonga'),
    ('TV','Tuvalu'),('VU','Vanuatu'),('WF','Wallis y Futuna'),('WS','Samoa'),
    ('AG','Antigua y Barbuda'),('AI','Anguila'),('AW','Aruba'),('BM','Bermudas'),
    ('BQ','Bonaire San Eustaquio y Saba'),('CW','Curazao'),('DM','Dominica'),('FK','Islas Malvinas'),
    ('GD','Granada'),('GF','Guayana Francesa'),('GL','Groenlandia'),('GP','Guadalupe'),
    ('KN','San Cristobal y Nieves'),('KY','Islas Caiman'),('LC','Santa Lucia'),('MF','San Martin'),
    ('MQ','Martinica'),('MS','Montserrat'),('PM','San Pedro y Miquelon'),('PR','Puerto Rico'),
    ('SX','Sint Maarten'),('TC','Islas Turcas y Caicos'),('VC','San Vicente y las Granadinas'),
    ('VG','Islas Virgenes Britanicas'),('VI','Islas Virgenes de EEUU'),
    ('BV','Isla Bouvet'),('GS','Georgia del Sur'),('HM','Islas Heard y McDonald'),
    ('IO','Territorio Britanico del Oceano Indico'),('PN','Islas Pitcairn'),('SH','Santa Elena'),
    ('SJ','Svalbard y Jan Mayen'),('TF','Territorios Australes Franceses'),('UM','Islas Ultramarinas de EEUU'),
    ('YT','Mayotte'),('RE','Reunion'),('AQ','Antartida'),('CC','Islas Cocos'),('CX','Isla de Navidad')
) AS v(PaisIso, Nombre)
WHERE NOT EXISTS (SELECT 1 FROM dbo.PaisPermitido p WHERE p.PaisIso = v.PaisIso);
GO
