using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Ways.Infrastructure.Persistencia.Migraciones
{
    /// <inheritdoc />
    public partial class ComprasCodigoProveedorEnLinea : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "codigo_proveedor",
                table: "items_comprobante_compra",
                type: "citext",
                maxLength: 50,
                nullable: true);

            migrationBuilder.AddCheckConstraint(
                name: "ck_items_comprobante_compra_codigo_proveedor_normalizado",
                table: "items_comprobante_compra",
                sql: "codigo_proveedor IS NULL OR (codigo_proveedor = btrim(codigo_proveedor) AND codigo_proveedor <> '')");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropCheckConstraint(
                name: "ck_items_comprobante_compra_codigo_proveedor_normalizado",
                table: "items_comprobante_compra");

            migrationBuilder.DropColumn(
                name: "codigo_proveedor",
                table: "items_comprobante_compra");
        }
    }
}
