using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Ways.Infrastructure.Persistencia.Migraciones
{
    /// <inheritdoc />
    public partial class ComprasLineasPorConcepto : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AlterColumn<int>(
                name: "id_articulo",
                table: "items_comprobante_compra",
                type: "integer",
                nullable: true,
                oldClrType: typeof(int),
                oldType: "integer");

            migrationBuilder.AddCheckConstraint(
                name: "ck_items_comprobante_compra_concepto_sin_efectos",
                table: "items_comprobante_compra",
                sql: "id_articulo IS NOT NULL OR (actualiza_costo = false AND codigo_lote IS NULL AND fecha_vencimiento IS NULL AND id_lote IS NULL AND bultos IS NULL AND unidades_por_bulto IS NULL AND precio_sugerido IS NULL)");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropCheckConstraint(
                name: "ck_items_comprobante_compra_concepto_sin_efectos",
                table: "items_comprobante_compra");

            migrationBuilder.AlterColumn<int>(
                name: "id_articulo",
                table: "items_comprobante_compra",
                type: "integer",
                nullable: false,
                defaultValue: 0,
                oldClrType: typeof(int),
                oldType: "integer",
                oldNullable: true);
        }
    }
}
