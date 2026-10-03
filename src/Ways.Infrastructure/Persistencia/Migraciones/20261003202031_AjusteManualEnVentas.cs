using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Ways.Infrastructure.Persistencia.Migraciones
{
    /// <inheritdoc />
    public partial class AjusteManualEnVentas : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<decimal>(
                name: "ajuste_manual",
                table: "items_comprobante_venta",
                type: "numeric(14,2)",
                nullable: false,
                defaultValue: 0m);

            migrationBuilder.AddColumn<decimal>(
                name: "ajuste_manual_porcentaje",
                table: "items_comprobante_venta",
                type: "numeric(5,2)",
                nullable: true);

            migrationBuilder.AddColumn<decimal>(
                name: "descuento_manual_total",
                table: "comprobantes_venta",
                type: "numeric(14,2)",
                nullable: false,
                defaultValue: 0m);

            migrationBuilder.AddColumn<decimal>(
                name: "recargo_manual_total",
                table: "comprobantes_venta",
                type: "numeric(14,2)",
                nullable: false,
                defaultValue: 0m);

            migrationBuilder.AddCheckConstraint(
                name: "ck_items_comprobante_venta_ajuste_manual_con_porcentaje",
                table: "items_comprobante_venta",
                sql: "ajuste_manual_porcentaje IS NOT NULL OR ajuste_manual = 0");

            migrationBuilder.AddCheckConstraint(
                name: "ck_items_comprobante_venta_ajuste_manual_porcentaje_valido",
                table: "items_comprobante_venta",
                sql: "ajuste_manual_porcentaje IS NULL OR (ajuste_manual_porcentaje <> 0 AND ajuste_manual_porcentaje BETWEEN -100 AND 100)");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropCheckConstraint(
                name: "ck_items_comprobante_venta_ajuste_manual_con_porcentaje",
                table: "items_comprobante_venta");

            migrationBuilder.DropCheckConstraint(
                name: "ck_items_comprobante_venta_ajuste_manual_porcentaje_valido",
                table: "items_comprobante_venta");

            migrationBuilder.DropColumn(
                name: "ajuste_manual",
                table: "items_comprobante_venta");

            migrationBuilder.DropColumn(
                name: "ajuste_manual_porcentaje",
                table: "items_comprobante_venta");

            migrationBuilder.DropColumn(
                name: "descuento_manual_total",
                table: "comprobantes_venta");

            migrationBuilder.DropColumn(
                name: "recargo_manual_total",
                table: "comprobantes_venta");
        }
    }
}
