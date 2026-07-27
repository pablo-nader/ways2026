drop table if exists codigos_barra;

CREATE TABLE `codigos_barra` (
    `id` INT NOT NULL AUTO_INCREMENT , 
    `codigo` VARCHAR(20) NOT NULL , 
    `id_articulo` INT NOT NULL , 
    `fecha_creacion` DATETIME NOT NULL DEFAULT CURRENT_TIMESTAMP , 
    `fecha_edicion` DATETIME NOT NULL DEFAULT CURRENT_TIMESTAMP , 
    `fecha_eliminacion` DATETIME NULL DEFAULT NULL , 
    PRIMARY KEY (`id`), 
    UNIQUE (`codigo`)
) ENGINE = InnoDB;

ALTER TABLE `codigos_barra` 
    ADD CONSTRAINT `codigos_barra_to_articulos` 
    FOREIGN KEY (`id_articulo`) REFERENCES `articulos`(`ID`) 
    ON DELETE CASCADE ON UPDATE CASCADE;

insert into codigos_barra (codigo, id_articulo)
select barra, ID
from articulos
where activo = 1;

