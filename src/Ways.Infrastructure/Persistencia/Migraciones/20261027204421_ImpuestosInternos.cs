using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Ways.Infrastructure.Persistencia.Migraciones
{
    /// <inheritdoc />
    public partial class ImpuestosInternos : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            // Solo DDL, sin backfill: el default 0 es el valor correcto para toda fila existente (ningún comprobante
            // ni artículo cargado hasta hoy discriminaba impuestos internos).
            migrationBuilder.AddColumn<decimal>(
                name: "impuesto_interno",
                table: "items_comprobante_compra",
                type: "numeric(14,2)",
                nullable: false,
                defaultValue: 0m);

            migrationBuilder.AddColumn<decimal>(
                name: "impuestos_internos",
                table: "comprobantes_compra",
                type: "numeric(14,2)",
                nullable: false,
                defaultValue: 0m);

            migrationBuilder.AddColumn<decimal>(
                name: "porcentaje_impuesto_interno",
                table: "articulos",
                type: "numeric(7,4)",
                nullable: false,
                defaultValue: 0m);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "impuesto_interno",
                table: "items_comprobante_compra");

            migrationBuilder.DropColumn(
                name: "impuestos_internos",
                table: "comprobantes_compra");

            migrationBuilder.DropColumn(
                name: "porcentaje_impuesto_interno",
                table: "articulos");
        }
    }
}
