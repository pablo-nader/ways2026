using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Ways.Infrastructure.Persistencia.Migraciones
{
    /// <inheritdoc />
    public partial class CostoListaPorEIvaEnArticulos : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<bool>(
                name: "costo_lista_incluye_iva",
                table: "articulos",
                type: "boolean",
                nullable: false,
                defaultValue: true);

            migrationBuilder.AddColumn<string>(
                name: "costo_lista_por",
                table: "articulos",
                type: "text",
                nullable: false,
                defaultValue: "Unidad");

            migrationBuilder.AddCheckConstraint(
                name: "ck_articulos_costo_lista_por",
                table: "articulos",
                sql: "costo_lista_por IN ('Unidad', 'Bulto')");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropCheckConstraint(
                name: "ck_articulos_costo_lista_por",
                table: "articulos");

            migrationBuilder.DropColumn(
                name: "costo_lista_incluye_iva",
                table: "articulos");

            migrationBuilder.DropColumn(
                name: "costo_lista_por",
                table: "articulos");
        }
    }
}
