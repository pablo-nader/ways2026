﻿using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Ways.Infrastructure.Persistencia.Migraciones
{
    /// <summary>
    /// Renombra el concepto Gasto a Pago en la base. Solo renombra: ningún objeto se borra ni se
    /// recrea, así que las filas, los ids y los saldos quedan intactos.
    /// </summary>
    public partial class RenombrarGastosAPagos : Migration
    {
        private static readonly string[] CheckDeLaTabla =
        [
            "caja_turno_requiere_turno", "importe_positivo", "movimiento_personal_es_sueldo"
        ];

        private static readonly string[] ForaneasDeLaTabla =
        [
            "area", "comprobante_compra", "empleado", "empresa", "medio_pago", "movimiento_personal",
            "proveedor", "punto_venta", "tenant", "turno"
        ];

        private static readonly string[] IndicesDeLaTabla =
        [
            "area", "comprobante_compra", "empleado", "empresa", "medio_pago", "movimiento_personal",
            "proveedor", "punto_venta_fecha", "tenant", "turno"
        ];

        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            // Tipos nativos de Postgres. Renombrar el tipo o un valor no reescribe ninguna fila.
            migrationBuilder.Sql("ALTER TYPE categoria_gasto RENAME TO categoria_pago;");
            migrationBuilder.Sql("ALTER TYPE origen_fondos_gasto RENAME TO origen_fondos_pago;");
            migrationBuilder.Sql("ALTER TYPE tipo_movimiento_tesoreria RENAME VALUE 'gasto' TO 'pago';");

            // Tabla, columnas y secuencia de identidad (Postgres no renombra la secuencia sola).
            migrationBuilder.Sql("ALTER TABLE gastos RENAME TO pagos;");
            migrationBuilder.RenameColumn(name: "id_gasto", table: "pagos", newName: "id_pago");
            migrationBuilder.RenameColumn(name: "id_gasto", table: "movimientos_tesoreria", newName: "id_pago");
            migrationBuilder.RenameColumn(
                name: "id_gasto", table: "movimientos_cuenta_corriente_proveedor", newName: "id_pago");
            migrationBuilder.Sql("ALTER SEQUENCE IF EXISTS gastos_id_gasto_seq RENAME TO pagos_id_pago_seq;");

            // Restricciones: la clave primaria y la alterna arrastran su índice al renombrarse.
            migrationBuilder.Sql("ALTER TABLE pagos RENAME CONSTRAINT pk_gastos TO pk_pagos;");
            migrationBuilder.Sql(
                "ALTER TABLE pagos RENAME CONSTRAINT ak_gastos_id_gasto_id_tenant TO ak_pagos_id_pago_id_tenant;");
            foreach (var sufijo in CheckDeLaTabla)
            {
                migrationBuilder.Sql($"ALTER TABLE pagos RENAME CONSTRAINT ck_gastos_{sufijo} TO ck_pagos_{sufijo};");
            }

            foreach (var sufijo in ForaneasDeLaTabla)
            {
                migrationBuilder.Sql($"ALTER TABLE pagos RENAME CONSTRAINT fk_gastos_{sufijo} TO fk_pagos_{sufijo};");
            }

            migrationBuilder.Sql(
                "ALTER TABLE movimientos_tesoreria RENAME CONSTRAINT fk_movimientos_tesoreria_gasto " +
                "TO fk_movimientos_tesoreria_pago;");
            migrationBuilder.Sql(
                "ALTER TABLE movimientos_cuenta_corriente_proveedor RENAME CONSTRAINT " +
                "fk_movimientos_cuenta_corriente_proveedor_gasto TO fk_movimientos_cuenta_corriente_proveedor_pago;");

            // Índices.
            foreach (var sufijo in IndicesDeLaTabla)
            {
                migrationBuilder.RenameIndex(name: $"ix_gastos_{sufijo}", table: "pagos", newName: $"ix_pagos_{sufijo}");
            }

            migrationBuilder.RenameIndex(
                name: "ix_movimientos_tesoreria_gasto", table: "movimientos_tesoreria",
                newName: "ix_movimientos_tesoreria_pago");
            migrationBuilder.RenameIndex(
                name: "ux_movimientos_tesoreria_id_gasto", table: "movimientos_tesoreria",
                newName: "ux_movimientos_tesoreria_id_pago");
            migrationBuilder.RenameIndex(
                name: "ix_movimientos_cuenta_corriente_proveedor_gasto",
                table: "movimientos_cuenta_corriente_proveedor",
                newName: "ix_movimientos_cuenta_corriente_proveedor_pago");

            // RLS: la policy sigue a la tabla, solo cambia su nombre para que sea coherente.
            migrationBuilder.Sql("ALTER POLICY gastos_tenant ON pagos RENAME TO pagos_tenant;");

            // Backfill de los códigos de auditoría para que el historial siga filtrable.
            // `auditoria` corre bajo FORCE ROW LEVEL SECURITY y el rol de aplicación no tiene
            // BYPASSRLS: sin `SET LOCAL app.acceso = 'plataforma'` el UPDATE tocaría 0 filas y
            // reportaría éxito (skill rls-migration-backfills). Idempotente: una corrida repetida
            // no encuentra filas con el código viejo.
            migrationBuilder.Sql(
                """
                SET LOCAL app.acceso = 'plataforma';

                UPDATE auditoria SET accion = 'pago.' || substr(accion, 7) WHERE accion LIKE 'gasto.%';
                UPDATE auditoria SET entidad = 'pago' WHERE entidad = 'gasto';
                """);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            // Mismo SET LOCAL que el Up: la reversión del backfill también pasa por RLS.
            migrationBuilder.Sql(
                """
                SET LOCAL app.acceso = 'plataforma';

                UPDATE auditoria SET entidad = 'gasto' WHERE entidad = 'pago' AND accion LIKE 'pago.%';
                UPDATE auditoria SET accion = 'gasto.' || substr(accion, 6) WHERE accion LIKE 'pago.%';
                """);

            migrationBuilder.Sql("ALTER POLICY pagos_tenant ON pagos RENAME TO gastos_tenant;");

            migrationBuilder.RenameIndex(
                name: "ix_movimientos_cuenta_corriente_proveedor_pago",
                table: "movimientos_cuenta_corriente_proveedor",
                newName: "ix_movimientos_cuenta_corriente_proveedor_gasto");
            migrationBuilder.RenameIndex(
                name: "ux_movimientos_tesoreria_id_pago", table: "movimientos_tesoreria",
                newName: "ux_movimientos_tesoreria_id_gasto");
            migrationBuilder.RenameIndex(
                name: "ix_movimientos_tesoreria_pago", table: "movimientos_tesoreria",
                newName: "ix_movimientos_tesoreria_gasto");
            foreach (var sufijo in IndicesDeLaTabla)
            {
                migrationBuilder.RenameIndex(name: $"ix_pagos_{sufijo}", table: "pagos", newName: $"ix_gastos_{sufijo}");
            }

            migrationBuilder.Sql(
                "ALTER TABLE movimientos_cuenta_corriente_proveedor RENAME CONSTRAINT " +
                "fk_movimientos_cuenta_corriente_proveedor_pago TO fk_movimientos_cuenta_corriente_proveedor_gasto;");
            migrationBuilder.Sql(
                "ALTER TABLE movimientos_tesoreria RENAME CONSTRAINT fk_movimientos_tesoreria_pago " +
                "TO fk_movimientos_tesoreria_gasto;");
            foreach (var sufijo in ForaneasDeLaTabla)
            {
                migrationBuilder.Sql($"ALTER TABLE pagos RENAME CONSTRAINT fk_pagos_{sufijo} TO fk_gastos_{sufijo};");
            }

            foreach (var sufijo in CheckDeLaTabla)
            {
                migrationBuilder.Sql($"ALTER TABLE pagos RENAME CONSTRAINT ck_pagos_{sufijo} TO ck_gastos_{sufijo};");
            }

            migrationBuilder.Sql(
                "ALTER TABLE pagos RENAME CONSTRAINT ak_pagos_id_pago_id_tenant TO ak_gastos_id_gasto_id_tenant;");
            migrationBuilder.Sql("ALTER TABLE pagos RENAME CONSTRAINT pk_pagos TO pk_gastos;");

            migrationBuilder.Sql("ALTER SEQUENCE IF EXISTS pagos_id_pago_seq RENAME TO gastos_id_gasto_seq;");
            migrationBuilder.RenameColumn(
                name: "id_pago", table: "movimientos_cuenta_corriente_proveedor", newName: "id_gasto");
            migrationBuilder.RenameColumn(name: "id_pago", table: "movimientos_tesoreria", newName: "id_gasto");
            migrationBuilder.RenameColumn(name: "id_pago", table: "pagos", newName: "id_gasto");
            migrationBuilder.Sql("ALTER TABLE pagos RENAME TO gastos;");

            migrationBuilder.Sql("ALTER TYPE tipo_movimiento_tesoreria RENAME VALUE 'pago' TO 'gasto';");
            migrationBuilder.Sql("ALTER TYPE origen_fondos_pago RENAME TO origen_fondos_gasto;");
            migrationBuilder.Sql("ALTER TYPE categoria_pago RENAME TO categoria_gasto;");
        }
    }
}
