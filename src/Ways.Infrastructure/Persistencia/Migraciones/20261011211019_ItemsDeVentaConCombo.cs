using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Ways.Infrastructure.Persistencia.Migraciones
{
    /// <inheritdoc />
    public partial class ItemsDeVentaConCombo : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<int>(
                name: "id_combo",
                table: "items_comprobante_venta",
                type: "integer",
                nullable: true);

            migrationBuilder.CreateIndex(
                name: "ix_items_comprobante_venta_combo",
                table: "items_comprobante_venta",
                columns: new[] { "id_combo", "id_tenant" });

            migrationBuilder.AddForeignKey(
                name: "fk_items_comprobante_venta_combo",
                table: "items_comprobante_venta",
                columns: new[] { "id_combo", "id_tenant" },
                principalTable: "combos",
                principalColumns: new[] { "id_combo", "id_tenant" },
                onDelete: ReferentialAction.Restrict);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropForeignKey(
                name: "fk_items_comprobante_venta_combo",
                table: "items_comprobante_venta");

            migrationBuilder.DropIndex(
                name: "ix_items_comprobante_venta_combo",
                table: "items_comprobante_venta");

            migrationBuilder.DropColumn(
                name: "id_combo",
                table: "items_comprobante_venta");
        }
    }
}
