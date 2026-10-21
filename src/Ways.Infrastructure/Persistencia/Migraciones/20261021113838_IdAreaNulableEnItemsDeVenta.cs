using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Ways.Infrastructure.Persistencia.Migraciones
{
    /// <inheritdoc />
    public partial class IdAreaNulableEnItemsDeVenta : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            // Solo DDL, sin backfill: las filas existentes conservan su área. NULL queda reservado a una línea de
            // concepto libre sin artículo (el envío); la FK compuesta a areas sigue igual.
            migrationBuilder.AlterColumn<int>(
                name: "id_area",
                table: "items_comprobante_venta",
                type: "integer",
                nullable: true,
                oldClrType: typeof(int),
                oldType: "integer");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            // Falla a propósito si ya hay líneas sin área (envíos): no hay un área que inventarles.
            migrationBuilder.AlterColumn<int>(
                name: "id_area",
                table: "items_comprobante_venta",
                type: "integer",
                nullable: false,
                oldClrType: typeof(int),
                oldType: "integer",
                oldNullable: true);
        }
    }
}
