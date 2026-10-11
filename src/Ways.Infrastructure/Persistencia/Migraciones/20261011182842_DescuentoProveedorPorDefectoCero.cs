using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Ways.Infrastructure.Persistencia.Migraciones
{
    /// <inheritdoc />
    public partial class DescuentoProveedorPorDefectoCero : Migration
    {
        /// <summary>Backfill de datos de <c>Up()</c>, expuesto para probarlo bajo <c>ways_app</c> (rls-migration-backfills).
        /// <c>articulos</c> corre bajo FORCE ROW LEVEL SECURITY y el rol de aplicación no tiene BYPASSRLS: sin
        /// <c>SET LOCAL app.acceso = 'plataforma'</c> en el mismo bloque, los UPDATE tocan 0 filas y reportan éxito.
        /// El descuento nulo pasa a 0 (nuevo default de la columna). El costo nominal se calcula solo donde falta y hay
        /// costo de lista —nunca se pisa uno guardado, que puede ser el efectivo de una compra— con la misma cuenta que
        /// <c>CalculadorDeCostoNominal</c>. Idempotente: una corrida repetida no encuentra filas.</summary>
        public const string SqlDelBackfill =
            """
            SET LOCAL app.acceso = 'plataforma';

            UPDATE articulos SET descuento_proveedor = 0 WHERE descuento_proveedor IS NULL;

            UPDATE articulos a
            SET costo_nominal = round(
                (CASE WHEN a.costo_lista_por = 'Bulto' THEN a.costo_lista / a.unidades_por_bulto ELSE a.costo_lista END)
                * (CASE WHEN a.costo_lista_incluye_iva THEN 1 ELSE 1 + ai.porcentaje / 100 END)
                * (1 - COALESCE(a.descuento_proveedor, 0) / 100),
                2)
            FROM alicuotas_iva ai
            WHERE ai.id_alicuota_iva = a.id_alicuota_iva
              AND a.costo_nominal IS NULL
              AND a.costo_lista IS NOT NULL
              AND (a.costo_lista_por <> 'Bulto' OR a.unidades_por_bulto > 0);
            """;

        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AlterColumn<decimal>(
                name: "descuento_proveedor",
                table: "articulos",
                type: "numeric(5,2)",
                nullable: true,
                defaultValue: 0m,
                oldClrType: typeof(decimal),
                oldType: "numeric(5,2)",
                oldNullable: true);

            migrationBuilder.Sql(SqlDelBackfill);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AlterColumn<decimal>(
                name: "descuento_proveedor",
                table: "articulos",
                type: "numeric(5,2)",
                nullable: true,
                oldClrType: typeof(decimal),
                oldType: "numeric(5,2)",
                oldNullable: true,
                oldDefaultValue: 0m);
        }
    }
}
