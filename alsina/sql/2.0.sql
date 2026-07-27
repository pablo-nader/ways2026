CREATE TABLE `roles` (
    `id` INT NOT NULL AUTO_INCREMENT , 
    `nombre` VARCHAR(50) NOT NULL , 
    `fecha_creacion` DATETIME NOT NULL DEFAULT CURRENT_TIMESTAMP , 
    `fecha_edicion` DATETIME NOT NULL DEFAULT CURRENT_TIMESTAMP , 
    `fecha_eliminacion` DATETIME NULL DEFAULT NULL , 
    PRIMARY KEY (`id`), 
    UNIQUE (`nombre`)
) ENGINE = InnoDB;

CREATE TABLE `usuario_rol_puntoventa` (
    `id` INT NOT NULL AUTO_INCREMENT , 
    `id_usuario` INT NOT NULL , 
    `id_rol` INT NOT NULL , 
    `id_punto_venta` INT NOT NULL , 
    PRIMARY KEY (`id`)
) ENGINE = InnoDB;

ALTER TABLE `usuario_rol_puntoventa` 
    ADD CONSTRAINT `FK_To_Punto_Venta` FOREIGN KEY (`id_punto_venta`) REFERENCES `puntos_venta`(`id`) 
    ON DELETE CASCADE ON UPDATE CASCADE; 

ALTER TABLE `usuario_rol_puntoventa` 
    ADD CONSTRAINT `FK_To_Rol` FOREIGN KEY (`id_rol`) REFERENCES `roles`(`id`) 
    ON DELETE CASCADE ON UPDATE CASCADE;
    
ALTER TABLE `usuario_rol_puntoventa` 
    ADD CONSTRAINT `FK_To_Usuario` FOREIGN KEY (`id_usuario`) REFERENCES `usuarios`(`id`) 
    ON DELETE CASCADE ON UPDATE CASCADE;

INSERT INTO `roles` (`id`, `nombre`, `fecha_creacion`, `fecha_edicion`, `fecha_eliminacion`) 
VALUES  (NULL, 'Administrador', current_timestamp(), current_timestamp(), NULL), 
        (NULL, 'Encargado', current_timestamp(), current_timestamp(), NULL), 
        (NULL, 'Vendedor', current_timestamp(), current_timestamp(), NULL);

rename table caja to areas;
ALTER TABLE `articulos` CHANGE `caja` `id_area` INT(11) NULL DEFAULT NULL;
ALTER TABLE `articulos` CHANGE `proveedor` `id_proveedor` INT(11) NULL DEFAULT NULL;
ALTER TABLE `articulos` CHANGE `marca` `id_marca` INT(11) NULL DEFAULT NULL;
ALTER TABLE `articulos` CHANGE `grupo` `id_grupo` INT(11) NULL DEFAULT NULL;
ALTER TABLE `gastos` CHANGE `caja` `id_area` INT(11) NULL DEFAULT NULL;

-- ALTER TABLE `articulos` ADD CONSTRAINT `articulo_to_area` FOREIGN KEY (`id_area`) REFERENCES `areas`(`id`) ON DELETE SET NULL ON UPDATE CASCADE; ALTER TABLE `articulos` ADD CONSTRAINT `articulo_to_grupo` FOREIGN KEY (`id_grupo`) REFERENCES `grupos`(`id`) ON DELETE SET NULL ON UPDATE CASCADE; ALTER TABLE `articulos` ADD CONSTRAINT `articulo_to_marca` FOREIGN KEY (`id_marca`) REFERENCES `marcas`(`id`) ON DELETE SET NULL ON UPDATE CASCADE;

ALTER TABLE `ventas` 
    ADD `id_punto_venta` INT NOT NULL DEFAULT '1' AFTER `obs`;

ALTER TABLE `ventas` 
    ADD CONSTRAINT `venta_to_punto_venta` FOREIGN KEY (`id_punto_venta`) REFERENCES `puntos_venta`(`id`) 
    ON DELETE RESTRICT ON UPDATE CASCADE;

ALTER TABLE `ventas` 
    CHANGE `idCaja` `id_caja` INT(11) NULL;

ALTER TABLE `ventas` 
    ADD CONSTRAINT `venta_to_caja` FOREIGN KEY (`id_caja`) REFERENCES `cajas`(`id`) 
    ON DELETE RESTRICT ON UPDATE RESTRICT;

ALTER TABLE `cajas` 
    ADD `id_punto_venta` INT NOT NULL DEFAULT '1' AFTER `retiros`;

ALTER TABLE `cajas` 
    ADD CONSTRAINT `caja_to_punto_venta` FOREIGN KEY (`id_punto_venta`) REFERENCES `puntos_venta`(`id`) 
    ON DELETE RESTRICT ON UPDATE CASCADE;

ALTER TABLE `cajav` 
    ADD `id_punto_venta` INT NOT NULL DEFAULT '1' AFTER `operador`;

ALTER TABLE `cajav` 
    ADD CONSTRAINT `cajav_to_punto_venta` FOREIGN KEY (`id_punto_venta`) REFERENCES `puntos_venta`(`id`) 
    ON DELETE RESTRICT ON UPDATE CASCADE;

ALTER TABLE `cajaz` 
    ADD `id_punto_venta` INT NOT NULL DEFAULT '1' AFTER `operador`;

ALTER TABLE `cajaz` 
    ADD CONSTRAINT `cajaz_to_punto_venta` FOREIGN KEY (`id_punto_venta`) REFERENCES `puntos_venta`(`id`) 
    ON DELETE RESTRICT ON UPDATE CASCADE;

ALTER TABLE `gastos` DROP FOREIGN KEY `gastos_ibfk_2`; 
ALTER TABLE `gastos` 
    ADD CONSTRAINT `gasto_to_usuario` FOREIGN KEY (`vendedor`) REFERENCES `usuarios`(`id`) 
    ON DELETE RESTRICT ON UPDATE CASCADE; 

ALTER TABLE `gastos` DROP FOREIGN KEY `gastos_ibfk_3`; 
ALTER TABLE `gastos` 
    ADD CONSTRAINT `gasto_to_caja` FOREIGN KEY (`idCaja`) REFERENCES `cajas`(`id`) 
    ON DELETE RESTRICT ON UPDATE CASCADE;

ALTER TABLE `gastos` 
    CHANGE `vendedor` `id_usuario` INT(11) NOT NULL;

ALTER TABLE `ventas` 
    CHANGE `vendedor` `id_usuario` INT(11) NOT NULL;

ALTER TABLE `ventas` 
    ADD CONSTRAINT `venta_to_usuario` FOREIGN KEY (`id_usuario`) REFERENCES `usuarios`(`id`) 
    ON DELETE RESTRICT ON UPDATE CASCADE;

ALTER TABLE `gastos` 
    CHANGE `idCaja` `id_caja` INT(11) NOT NULL;

ALTER TABLE `gastos` 
    ADD `id_punto_venta` INT NOT NULL DEFAULT '1' AFTER `id_caja`;

ALTER TABLE `gastos` 
    ADD CONSTRAINT `gasto_to_punto_venta` FOREIGN KEY (`id_punto_venta`) REFERENCES `puntos_venta`(`id`) 
    ON DELETE RESTRICT ON UPDATE CASCADE;

ALTER TABLE `cajaz` 
    CHANGE `operador` `id_usuario` INT(11) NOT NULL;

ALTER TABLE `cajaz` 
    ADD CONSTRAINT `cajaz_to_usuario` FOREIGN KEY (`id_usuario`) 
    REFERENCES `usuarios`(`id`) ON DELETE RESTRICT ON UPDATE CASCADE;

update gastos set id_area = 1 where id_area not in (2, 3, 4, 5, 6);

ALTER TABLE `gastos` 
    ADD CONSTRAINT `gasto_to_area` FOREIGN KEY (`id_area`) REFERENCES `areas`(`id`) 
    ON DELETE RESTRICT ON UPDATE CASCADE;

ALTER TABLE `cajas` 
    CHANGE `operador` `id_usuario` INT(11) NOT NULL;

update cajas set id_usuario = 2 where id_usuario not in (select id from usuarios);

ALTER TABLE `cajas` 
    ADD CONSTRAINT `caja_to_usuario` FOREIGN KEY (`id_usuario`) REFERENCES `usuarios`(`id`) 
    ON DELETE RESTRICT ON UPDATE CASCADE;

ALTER TABLE `cajav` 
    CHANGE `operador` `id_usuario` INT(11) NOT NULL;

ALTER TABLE `cajav` 
    ADD CONSTRAINT `cajav_to_usuario` FOREIGN KEY (`id_usuario`) REFERENCES `usuarios`(`id`) 
    ON DELETE RESTRICT ON UPDATE CASCADE;

update articulos set id_area = 2 where id_area not in (2, 3, 4, 5, 6);

ALTER TABLE `articulos` 
    ADD CONSTRAINT `articulo_to_area` FOREIGN KEY (`id_area`) REFERENCES `areas`(`id`) 
    ON DELETE RESTRICT ON UPDATE CASCADE;

update articulos set id_marca = NULL where id_marca not in (select id from marcas);

ALTER TABLE `articulos` 
    ADD CONSTRAINT `articulo_to_marca` FOREIGN KEY (`id_marca`) 
    REFERENCES `marcas`(`id`) ON DELETE SET NULL ON UPDATE CASCADE;

update articulos set id_grupo = NULL where id_grupo not in (select id from grupos);
update articulos set id_proveedor = NULL where id_proveedor not in (select id from proveedores);

ALTER TABLE `articulos` 
    ADD CONSTRAINT `articulo_to_grupo` FOREIGN KEY (`id_grupo`) REFERENCES `grupos`(`id`) 
    ON DELETE SET NULL ON UPDATE CASCADE; 

ALTER TABLE `articulos` 
    ADD CONSTRAINT `articulo_to_proveedor` FOREIGN KEY (`id_proveedor`) REFERENCES `proveedores`(`id`) 
    ON DELETE SET NULL ON UPDATE CASCADE;

UPDATE `puntos_venta` SET `facebook` = 'WAYS Autoservicio' WHERE `puntos_venta`.`id` in (1, 2);