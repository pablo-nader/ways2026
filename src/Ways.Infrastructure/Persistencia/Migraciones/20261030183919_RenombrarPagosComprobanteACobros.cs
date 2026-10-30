using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Ways.Infrastructure.Persistencia.Migraciones
{
    /// <summary>
    /// Renombra pagos_comprobante a cobros. Solo renombra: ningún objeto se borra ni se recrea, así
    /// que las filas, los ids y los movimientos de cuenta corriente enlazados quedan intactos.
    /// </summary>
    public partial class RenombrarPagosComprobanteACobros : Migration
    {
        private static readonly string[] ForaneasDeLaTabla = ["comprobante", "medio_pago", "tenant"];

        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            // Tabla, columnas y secuencia de identidad (Postgres no renombra la secuencia sola).
            migrationBuilder.Sql("ALTER TABLE pagos_comprobante RENAME TO cobros;");
            migrationBuilder.RenameColumn(name: "id_pago", table: "cobros", newName: "id_cobro");
            migrationBuilder.RenameColumn(
                name: "id_pago_comprobante", table: "movimientos_cuenta_corriente", newName: "id_cobro");
            migrationBuilder.Sql(
                "ALTER SEQUENCE IF EXISTS pagos_comprobante_id_pago_seq RENAME TO cobros_id_cobro_seq;");

            // Restricciones: la clave primaria y la alterna arrastran su índice al renombrarse. El CHECK
            // de movimientos_cuenta_corriente que nombra la columna sigue vigente: Postgres reescribe su
            // expresión al renombrar la columna.
            migrationBuilder.Sql("ALTER TABLE cobros RENAME CONSTRAINT pk_pagos_comprobante TO pk_cobros;");
            migrationBuilder.Sql(
                "ALTER TABLE cobros RENAME CONSTRAINT ak_pagos_comprobante_id_pago_id_tenant " +
                "TO ak_cobros_id_cobro_id_tenant;");
            migrationBuilder.Sql(
                "ALTER TABLE cobros RENAME CONSTRAINT ck_pagos_comprobante_importe_no_negativo " +
                "TO ck_cobros_importe_no_negativo;");
            migrationBuilder.Sql(
                "ALTER TABLE cobros RENAME CONSTRAINT ck_pagos_comprobante_vuelto_no_negativo " +
                "TO ck_cobros_vuelto_no_negativo;");
            foreach (var sufijo in ForaneasDeLaTabla)
            {
                migrationBuilder.Sql(
                    $"ALTER TABLE cobros RENAME CONSTRAINT fk_pagos_comprobante_{sufijo} TO fk_cobros_{sufijo};");
            }

            migrationBuilder.Sql(
                "ALTER TABLE movimientos_cuenta_corriente RENAME CONSTRAINT " +
                "fk_movimientos_cuenta_corriente_pago_comprobante TO fk_movimientos_cuenta_corriente_cobro;");

            // Índices.
            foreach (var sufijo in ForaneasDeLaTabla)
            {
                migrationBuilder.RenameIndex(
                    name: $"ix_pagos_comprobante_{sufijo}", table: "cobros", newName: $"ix_cobros_{sufijo}");
            }

            migrationBuilder.RenameIndex(
                name: "ix_movimientos_cuenta_corriente_pago_comprobante",
                table: "movimientos_cuenta_corriente",
                newName: "ix_movimientos_cuenta_corriente_cobro");

            // RLS: la policy sigue a la tabla, solo cambia su nombre para que sea coherente.
            migrationBuilder.Sql("ALTER POLICY pagos_comprobante_tenant ON cobros RENAME TO cobros_tenant;");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql("ALTER POLICY cobros_tenant ON cobros RENAME TO pagos_comprobante_tenant;");

            migrationBuilder.RenameIndex(
                name: "ix_movimientos_cuenta_corriente_cobro",
                table: "movimientos_cuenta_corriente",
                newName: "ix_movimientos_cuenta_corriente_pago_comprobante");
            foreach (var sufijo in ForaneasDeLaTabla)
            {
                migrationBuilder.RenameIndex(
                    name: $"ix_cobros_{sufijo}", table: "cobros", newName: $"ix_pagos_comprobante_{sufijo}");
            }

            migrationBuilder.Sql(
                "ALTER TABLE movimientos_cuenta_corriente RENAME CONSTRAINT " +
                "fk_movimientos_cuenta_corriente_cobro TO fk_movimientos_cuenta_corriente_pago_comprobante;");
            foreach (var sufijo in ForaneasDeLaTabla)
            {
                migrationBuilder.Sql(
                    $"ALTER TABLE cobros RENAME CONSTRAINT fk_cobros_{sufijo} TO fk_pagos_comprobante_{sufijo};");
            }

            migrationBuilder.Sql(
                "ALTER TABLE cobros RENAME CONSTRAINT ck_cobros_vuelto_no_negativo " +
                "TO ck_pagos_comprobante_vuelto_no_negativo;");
            migrationBuilder.Sql(
                "ALTER TABLE cobros RENAME CONSTRAINT ck_cobros_importe_no_negativo " +
                "TO ck_pagos_comprobante_importe_no_negativo;");
            migrationBuilder.Sql(
                "ALTER TABLE cobros RENAME CONSTRAINT ak_cobros_id_cobro_id_tenant " +
                "TO ak_pagos_comprobante_id_pago_id_tenant;");
            migrationBuilder.Sql("ALTER TABLE cobros RENAME CONSTRAINT pk_cobros TO pk_pagos_comprobante;");

            migrationBuilder.Sql(
                "ALTER SEQUENCE IF EXISTS cobros_id_cobro_seq RENAME TO pagos_comprobante_id_pago_seq;");
            migrationBuilder.RenameColumn(
                name: "id_cobro", table: "movimientos_cuenta_corriente", newName: "id_pago_comprobante");
            migrationBuilder.RenameColumn(name: "id_cobro", table: "cobros", newName: "id_pago");
            migrationBuilder.Sql("ALTER TABLE cobros RENAME TO pagos_comprobante;");
        }
    }
}
