using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Ways.Infrastructure.Persistencia.Migraciones
{
    /// <inheritdoc />
    public partial class PagoDePersonal : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            // El valor nuevo del enum va con ADD VALUE IF NOT EXISTS en vez del AlterDatabase que genera EF: Postgres
            // no soporta DROP VALUE, así que tras un Down el valor sigue ahí y un Up posterior fallaría. Ningún
            // statement de esta migración nombra 'descuento_personal' (Postgres no deja usar un valor en la
            // transacción que lo agregó): los CHECK que lo nombran están en ChecksDelDescuentoDePersonal.
            migrationBuilder.Sql("ALTER TYPE tipo_movimiento_cc ADD VALUE IF NOT EXISTS 'descuento_personal';");

            migrationBuilder.AddColumn<int>(
                name: "id_movimiento_personal",
                table: "movimientos_cuenta_corriente",
                type: "integer",
                nullable: true);

            migrationBuilder.AddColumn<int>(
                name: "id_movimiento_personal",
                table: "gastos",
                type: "integer",
                nullable: true);

            migrationBuilder.AddUniqueConstraint(
                name: "ak_movimientos_personal_id_movimiento_id_tenant",
                table: "movimientos_personal",
                columns: new[] { "id_movimiento", "id_tenant" });

            migrationBuilder.CreateIndex(
                name: "ux_movimientos_cuenta_corriente_movimiento_personal",
                table: "movimientos_cuenta_corriente",
                columns: new[] { "id_movimiento_personal", "id_tenant" },
                unique: true,
                filter: "id_movimiento_personal IS NOT NULL");

            migrationBuilder.CreateIndex(
                name: "ix_gastos_movimiento_personal",
                table: "gastos",
                columns: new[] { "id_movimiento_personal", "id_tenant" });

            migrationBuilder.AddCheckConstraint(
                name: "ck_gastos_movimiento_personal_es_sueldo",
                table: "gastos",
                sql: "id_movimiento_personal IS NULL OR categoria = 'sueldos'");

            migrationBuilder.AddForeignKey(
                name: "fk_gastos_movimiento_personal",
                table: "gastos",
                columns: new[] { "id_movimiento_personal", "id_tenant" },
                principalTable: "movimientos_personal",
                principalColumns: new[] { "id_movimiento", "id_tenant" },
                onDelete: ReferentialAction.Restrict);

            migrationBuilder.AddForeignKey(
                name: "fk_movimientos_cuenta_corriente_movimiento_personal",
                table: "movimientos_cuenta_corriente",
                columns: new[] { "id_movimiento_personal", "id_tenant" },
                principalTable: "movimientos_personal",
                principalColumns: new[] { "id_movimiento", "id_tenant" },
                onDelete: ReferentialAction.Restrict);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropForeignKey(
                name: "fk_gastos_movimiento_personal",
                table: "gastos");

            migrationBuilder.DropForeignKey(
                name: "fk_movimientos_cuenta_corriente_movimiento_personal",
                table: "movimientos_cuenta_corriente");

            migrationBuilder.DropUniqueConstraint(
                name: "ak_movimientos_personal_id_movimiento_id_tenant",
                table: "movimientos_personal");

            migrationBuilder.DropIndex(
                name: "ux_movimientos_cuenta_corriente_movimiento_personal",
                table: "movimientos_cuenta_corriente");

            migrationBuilder.DropIndex(
                name: "ix_gastos_movimiento_personal",
                table: "gastos");

            migrationBuilder.DropCheckConstraint(
                name: "ck_gastos_movimiento_personal_es_sueldo",
                table: "gastos");

            migrationBuilder.DropColumn(
                name: "id_movimiento_personal",
                table: "movimientos_cuenta_corriente");

            migrationBuilder.DropColumn(
                name: "id_movimiento_personal",
                table: "gastos");

            // Sin reversa de tipo_movimiento_cc: Postgres no soporta DROP VALUE, así que 'descuento_personal'
            // queda como miembro muerto tras el rollback; el resto de la reversa sí se ejecuta. Mismo criterio que
            // RemitosEtapa17 con 'remito'.
        }
    }
}
