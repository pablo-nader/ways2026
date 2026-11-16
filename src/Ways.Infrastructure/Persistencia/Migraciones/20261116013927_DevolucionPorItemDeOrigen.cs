using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Ways.Infrastructure.Persistencia.Migraciones
{
    /// <inheritdoc />
    public partial class DevolucionPorItemDeOrigen : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            // Solo DDL, sin backfill: las notas de crédito anteriores quedan con la columna en NULL y su consumo se sigue
            // repartiendo por artículo (DevolucionDeTicket). El origen lo escribe el servidor, nunca el cliente.
            migrationBuilder.AddColumn<int>(
                name: "id_item_comprobante_venta_origen",
                table: "items_comprobante_venta",
                type: "integer",
                nullable: true);

            migrationBuilder.AddUniqueConstraint(
                name: "ak_items_comprobante_venta_id_item_id_tenant",
                table: "items_comprobante_venta",
                columns: new[] { "id_item", "id_tenant" });

            migrationBuilder.CreateIndex(
                name: "ix_items_comprobante_venta_item_origen",
                table: "items_comprobante_venta",
                columns: new[] { "id_item_comprobante_venta_origen", "id_tenant" });

            migrationBuilder.AddForeignKey(
                name: "fk_items_comprobante_venta_item_origen",
                table: "items_comprobante_venta",
                columns: new[] { "id_item_comprobante_venta_origen", "id_tenant" },
                principalTable: "items_comprobante_venta",
                principalColumns: new[] { "id_item", "id_tenant" },
                onDelete: ReferentialAction.Restrict);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropForeignKey(
                name: "fk_items_comprobante_venta_item_origen",
                table: "items_comprobante_venta");

            migrationBuilder.DropUniqueConstraint(
                name: "ak_items_comprobante_venta_id_item_id_tenant",
                table: "items_comprobante_venta");

            migrationBuilder.DropIndex(
                name: "ix_items_comprobante_venta_item_origen",
                table: "items_comprobante_venta");

            migrationBuilder.DropColumn(
                name: "id_item_comprobante_venta_origen",
                table: "items_comprobante_venta");
        }
    }
}
