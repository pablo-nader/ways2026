using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Ways.Infrastructure.Persistencia.Migraciones
{
    /// <inheritdoc />
    public partial class CuentaCorrienteEtapa7 : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<int>(
                name: "id_movimiento_actualizacion",
                table: "movimientos_cuenta_corriente",
                type: "integer",
                nullable: true);

            migrationBuilder.AddUniqueConstraint(
                name: "ak_movimientos_cuenta_corriente_id_movimiento_id_tenant",
                table: "movimientos_cuenta_corriente",
                columns: new[] { "id_movimiento", "id_tenant" });

            migrationBuilder.CreateIndex(
                name: "ix_movimientos_cuenta_corriente_actualizacion",
                table: "movimientos_cuenta_corriente",
                columns: new[] { "id_movimiento_actualizacion", "id_tenant" });

            migrationBuilder.CreateIndex(
                name: "ix_movimientos_cuenta_corriente_consumos_pendientes",
                table: "movimientos_cuenta_corriente",
                columns: new[] { "id_cliente", "id_tenant" },
                filter: "tipo = 'consumo' AND id_movimiento_actualizacion IS NULL");

            migrationBuilder.AddForeignKey(
                name: "fk_movimientos_cuenta_corriente_actualizacion",
                table: "movimientos_cuenta_corriente",
                columns: new[] { "id_movimiento_actualizacion", "id_tenant" },
                principalTable: "movimientos_cuenta_corriente",
                principalColumns: new[] { "id_movimiento", "id_tenant" },
                onDelete: ReferentialAction.Restrict);

            // design: Table Shapes B — el tipo de comprobante RC (pago a cuenta) se siembra acá
            // de forma idempotente porque InicializadorDeBaseDeDatos solo siembra
            // tipos_comprobante cuando la tabla está vacía (InicializadorDeBaseDeDatos.cs:417):
            // una base ya migrada (stage 6 en adelante) nunca recibiría la fila de otro modo.
            //
            // DESVIACIÓN DOCUMENTADA vs. task 1.3 (detectada corriendo la suite completa, no
            // solo el test dedicado): el guard original era "WHERE NOT EXISTS (... codigo =
            // 'RC')" sin más. Contra una base GENUINAMENTE vacía (el camino normal de
            // Database.MigrateAsync() antes de que corra el seeder), ese INSERT deja UNA fila
            // en tipos_comprobante durante la migración — y el chequeo de "tabla vacía" que
            // hace el seeder (:417) pasa a ver la tabla como no-vacía, saltándose las otras
            // diez filas de TiposComprobanteBase (FA…PRE) por completo. Se agrega "EXISTS
            // (SELECT 1 FROM tipos_comprobante)" al guard: el INSERT de esta migración solo
            // corre si la tabla YA tenía catálogo (el escenario real que existe para cubrir —
            // una base migrada desde stage 6), nunca contra una base recién creada, que queda
            // intacta para que el seeder la puebla completa y atómica (con RC ya incluido en
            // TiposComprobanteBase).
            migrationBuilder.Sql(
                """
                INSERT INTO tipos_comprobante (clase, codigo, nombre, letra, signo, discrimina_iva, es_fiscal, afecta_stock, activo, created_at, updated_at)
                SELECT 'venta', 'RC', 'Recibo de cobranza', NULL, 1, false, false, false, true, now(), now()
                WHERE EXISTS (SELECT 1 FROM tipos_comprobante)
                  AND NOT EXISTS (SELECT 1 FROM tipos_comprobante WHERE codigo = 'RC');
                """);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            // design: Migration/Rollout — desactiva en vez de borrar, para que un RC ya emitido
            // (y su movimiento Pago) sigan siendo legibles después de un rollback.
            migrationBuilder.Sql("UPDATE tipos_comprobante SET activo = false WHERE codigo = 'RC';");

            migrationBuilder.DropForeignKey(
                name: "fk_movimientos_cuenta_corriente_actualizacion",
                table: "movimientos_cuenta_corriente");

            migrationBuilder.DropUniqueConstraint(
                name: "ak_movimientos_cuenta_corriente_id_movimiento_id_tenant",
                table: "movimientos_cuenta_corriente");

            migrationBuilder.DropIndex(
                name: "ix_movimientos_cuenta_corriente_actualizacion",
                table: "movimientos_cuenta_corriente");

            migrationBuilder.DropIndex(
                name: "ix_movimientos_cuenta_corriente_consumos_pendientes",
                table: "movimientos_cuenta_corriente");

            migrationBuilder.DropColumn(
                name: "id_movimiento_actualizacion",
                table: "movimientos_cuenta_corriente");
        }
    }
}
