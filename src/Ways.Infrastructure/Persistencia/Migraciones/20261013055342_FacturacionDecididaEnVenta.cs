using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Ways.Infrastructure.Persistencia.Migraciones
{
    /// <inheritdoc />
    public partial class FacturacionDecididaEnVenta : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            // Toda venta que existe al migrar queda leída como ya decidida, sin reescribir la tabla: now() es STABLE, así que
            // PostgreSQL lo evalúa una sola vez en el ALTER y guarda ese valor como "faltante" de la columna (solo metadata).
            // Quitar el valor por defecto después no toca las filas existentes y hace que las nuevas nazcan en NULL
            // (decisión debida). El modelo de EF describe una columna nullable sin valor por defecto.
            // Las columnas de intentos fallidos también son solo metadata: un entero con DEFAULT constante y un nullable.
            migrationBuilder.Sql(
                "ALTER TABLE comprobantes_venta ADD COLUMN facturacion_decidida_en timestamp with time zone NULL DEFAULT now();");
            migrationBuilder.Sql(
                "ALTER TABLE comprobantes_venta ALTER COLUMN facturacion_decidida_en DROP DEFAULT;");

            migrationBuilder.AddColumn<int>(
                name: "facturacion_intentos",
                table: "comprobantes_venta",
                type: "integer",
                nullable: false,
                defaultValue: 0);

            migrationBuilder.AddColumn<DateTimeOffset>(
                name: "facturacion_proximo_intento_en",
                table: "comprobantes_venta",
                type: "timestamp with time zone",
                nullable: true);

            migrationBuilder.CreateIndex(
                name: "ix_comprobantes_venta_decision_debida",
                table: "comprobantes_venta",
                columns: new[] { "id_punto_venta", "id_comprobante_venta" },
                filter: "facturacion_decidida_en IS NULL AND id_venta_origen IS NULL");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "ix_comprobantes_venta_decision_debida",
                table: "comprobantes_venta");

            migrationBuilder.DropColumn(
                name: "facturacion_decidida_en",
                table: "comprobantes_venta");

            migrationBuilder.DropColumn(
                name: "facturacion_intentos",
                table: "comprobantes_venta");

            migrationBuilder.DropColumn(
                name: "facturacion_proximo_intento_en",
                table: "comprobantes_venta");
        }
    }
}
