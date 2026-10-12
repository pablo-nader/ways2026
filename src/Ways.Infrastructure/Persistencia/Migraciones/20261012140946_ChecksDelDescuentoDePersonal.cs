using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Ways.Infrastructure.Persistencia.Migraciones
{
    /// <inheritdoc />
    public partial class ChecksDelDescuentoDePersonal : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddCheckConstraint(
                name: "ck_movimientos_cuenta_corriente_descuento_personal",
                table: "movimientos_cuenta_corriente",
                sql: "tipo <> 'descuento_personal' OR (id_movimiento_personal IS NOT NULL AND id_comprobante_venta IS NULL AND id_pago_comprobante IS NULL AND importe < 0)");

            migrationBuilder.AddCheckConstraint(
                name: "ck_movimientos_cuenta_corriente_vinculo_personal",
                table: "movimientos_cuenta_corriente",
                sql: "id_movimiento_personal IS NULL OR tipo = 'descuento_personal'");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropCheckConstraint(
                name: "ck_movimientos_cuenta_corriente_descuento_personal",
                table: "movimientos_cuenta_corriente");

            migrationBuilder.DropCheckConstraint(
                name: "ck_movimientos_cuenta_corriente_vinculo_personal",
                table: "movimientos_cuenta_corriente");
        }
    }
}
