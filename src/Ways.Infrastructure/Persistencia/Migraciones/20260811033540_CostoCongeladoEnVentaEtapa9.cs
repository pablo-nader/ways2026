using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Ways.Infrastructure.Persistencia.Migraciones
{
    /// <inheritdoc />
    public partial class CostoCongeladoEnVentaEtapa9 : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<decimal>(
                name: "costo_unitario",
                table: "items_comprobante_venta",
                type: "numeric(14,2)",
                nullable: true);

            migrationBuilder.AddColumn<bool>(
                name: "costo_es_estimado",
                table: "items_comprobante_venta",
                type: "boolean",
                nullable: false,
                defaultValue: false);

            migrationBuilder.AddCheckConstraint(
                name: "ck_items_comprobante_venta_costo_no_negativo",
                table: "items_comprobante_venta",
                sql: "costo_unitario IS NULL OR costo_unitario >= 0");

            migrationBuilder.AddCheckConstraint(
                name: "ck_items_comprobante_venta_estimado_con_costo",
                table: "items_comprobante_venta",
                sql: "NOT costo_es_estimado OR costo_unitario IS NOT NULL");

            // Backfill de una sola vez (stage 9, decisión 6): cada tabla de tenant corre bajo
            // FORCE ROW LEVEL SECURITY y el rol de aplicación no tiene BYPASSRLS en Producción
            // (InicializadorDeBaseDeDatos.VerificarRolSinBypassAsync), así que un UPDATE plano
            // afectaría CERO filas y reportaría éxito. El camino de deploy (WaysDbContextFactory,
            // el que usa `dotnet ef database update`) no registra el interceptor de tenant — por
            // eso el SET LOCAL vive en este mismo bloque Sql(), nunca fuera de él.
            // Idempotente por construcción: WHERE i.costo_unitario IS NULL excluye las filas ya
            // completadas por una corrida anterior.
            migrationBuilder.Sql(
                """
                SET LOCAL app.acceso = 'plataforma';

                UPDATE items_comprobante_venta i
                   SET costo_unitario    = a.costo_nominal,
                       costo_es_estimado = true
                  FROM articulos a
                 WHERE a.id_articulo = i.id_articulo
                   AND a.id_tenant   = i.id_tenant
                   AND i.id_articulo IS NOT NULL
                   AND a.costo_nominal IS NOT NULL
                   AND i.costo_unitario IS NULL;
                """);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropCheckConstraint(
                name: "ck_items_comprobante_venta_costo_no_negativo",
                table: "items_comprobante_venta");

            migrationBuilder.DropCheckConstraint(
                name: "ck_items_comprobante_venta_estimado_con_costo",
                table: "items_comprobante_venta");

            migrationBuilder.DropColumn(
                name: "costo_es_estimado",
                table: "items_comprobante_venta");

            migrationBuilder.DropColumn(
                name: "costo_unitario",
                table: "items_comprobante_venta");
        }
    }
}
