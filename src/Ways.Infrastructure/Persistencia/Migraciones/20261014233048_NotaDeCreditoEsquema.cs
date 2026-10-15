using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Ways.Infrastructure.Persistencia.Migraciones
{
    /// <inheritdoc />
    public partial class NotaDeCreditoEsquema : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "ux_facturaciones_de_venta_comprobante",
                table: "facturaciones_de_venta");

            migrationBuilder.DropIndex(
                name: "ux_comprobantes_venta_venta_origen",
                table: "comprobantes_venta");

            migrationBuilder.AddColumn<int>(
                name: "id_facturacion_origen",
                table: "facturaciones_de_venta",
                type: "integer",
                nullable: true);

            migrationBuilder.AddUniqueConstraint(
                name: "ak_facturaciones_de_venta_id_facturacion_venta_id_tenant",
                table: "facturaciones_de_venta",
                columns: new[] { "id_facturacion_venta", "id_tenant" });

            migrationBuilder.CreateIndex(
                name: "ux_facturaciones_de_venta_comprobante",
                table: "facturaciones_de_venta",
                columns: new[] { "id_comprobante_venta", "id_tenant" },
                unique: true,
                filter: "id_facturacion_origen IS NULL");

            migrationBuilder.CreateIndex(
                name: "ux_facturaciones_de_venta_origen",
                table: "facturaciones_de_venta",
                columns: new[] { "id_facturacion_origen", "id_tenant" },
                unique: true,
                filter: "id_facturacion_origen IS NOT NULL");

            migrationBuilder.AddCheckConstraint(
                name: "ck_facturaciones_de_venta_nc_sin_aportes",
                table: "facturaciones_de_venta",
                sql: "id_facturacion_origen IS NULL OR aportes = '[]'::jsonb");

            migrationBuilder.AddCheckConstraint(
                name: "ck_facturaciones_de_venta_origen_distinto",
                table: "facturaciones_de_venta",
                sql: "id_facturacion_origen IS NULL OR id_facturacion_origen <> id_facturacion_venta");

            migrationBuilder.CreateIndex(
                name: "ux_comprobantes_venta_nota_credito_espejo",
                table: "comprobantes_venta",
                columns: new[] { "id_comprobante_asociado", "id_tenant" },
                unique: true,
                filter: "id_venta_origen IS NOT NULL AND id_comprobante_asociado IS NOT NULL");

            migrationBuilder.CreateIndex(
                name: "ux_comprobantes_venta_venta_origen",
                table: "comprobantes_venta",
                columns: new[] { "id_venta_origen", "id_tenant" },
                unique: true,
                filter: "id_venta_origen IS NOT NULL AND id_comprobante_asociado IS NULL");

            migrationBuilder.AddForeignKey(
                name: "fk_facturaciones_de_venta_origen",
                table: "facturaciones_de_venta",
                columns: new[] { "id_facturacion_origen", "id_tenant" },
                principalTable: "facturaciones_de_venta",
                principalColumns: new[] { "id_facturacion_venta", "id_tenant" },
                onDelete: ReferentialAction.Restrict);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropForeignKey(
                name: "fk_facturaciones_de_venta_origen",
                table: "facturaciones_de_venta");

            migrationBuilder.DropUniqueConstraint(
                name: "ak_facturaciones_de_venta_id_facturacion_venta_id_tenant",
                table: "facturaciones_de_venta");

            migrationBuilder.DropIndex(
                name: "ux_facturaciones_de_venta_comprobante",
                table: "facturaciones_de_venta");

            migrationBuilder.DropIndex(
                name: "ux_facturaciones_de_venta_origen",
                table: "facturaciones_de_venta");

            migrationBuilder.DropCheckConstraint(
                name: "ck_facturaciones_de_venta_nc_sin_aportes",
                table: "facturaciones_de_venta");

            migrationBuilder.DropCheckConstraint(
                name: "ck_facturaciones_de_venta_origen_distinto",
                table: "facturaciones_de_venta");

            migrationBuilder.DropIndex(
                name: "ux_comprobantes_venta_nota_credito_espejo",
                table: "comprobantes_venta");

            migrationBuilder.DropIndex(
                name: "ux_comprobantes_venta_venta_origen",
                table: "comprobantes_venta");

            migrationBuilder.DropColumn(
                name: "id_facturacion_origen",
                table: "facturaciones_de_venta");

            migrationBuilder.CreateIndex(
                name: "ux_facturaciones_de_venta_comprobante",
                table: "facturaciones_de_venta",
                columns: new[] { "id_comprobante_venta", "id_tenant" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "ux_comprobantes_venta_venta_origen",
                table: "comprobantes_venta",
                columns: new[] { "id_venta_origen", "id_tenant" },
                unique: true,
                filter: "id_venta_origen IS NOT NULL");
        }
    }
}
