using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Ways.Infrastructure.Persistencia.Migraciones
{
    /// <inheritdoc />
    public partial class CanceladoPorMercadoPagoEnPropuestas : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<bool>(
                name: "cancelado_por_mercadopago",
                table: "propuestas_tesoreria_mercadopago",
                type: "boolean",
                nullable: false,
                defaultValue: false);

            migrationBuilder.CreateIndex(
                name: "ix_filas_conciliacion_mercadopago_cancelaciones",
                table: "filas_conciliacion_mercadopago",
                columns: new[] { "id_empresa", "tipo_transaccion", "source_id" });

            migrationBuilder.Sql(SqlDeBackfill);
        }

        /// <summary>Lleva a la base lo que el generador hace en vivo con una cancelación (<c>WITHDRAWAL_CANCEL</c> de la empresa con el SOURCE_ID del retiro, en
        /// cualquier conciliación): el retiro pendiente se descarta con el motivo de la cancelación (sin usuario: no lo resolvió nadie), el aplicado sin reversión
        /// recibe la suya, pendiente y apuntando a la primera fila de cancelación, y toda propuesta que una cancelación tocó queda marcada. No escribe auditoría.
        /// Con el GUC de plataforma (rls-migration-backfills): sin él el rol de la aplicación no ve ninguna fila. Es idempotente: cada sentencia salta lo ya hecho.</summary>
        public const string SqlDeBackfill =
            """
            SET LOCAL app.acceso = 'plataforma';
            UPDATE propuestas_tesoreria_mercadopago p
            SET estado = 'descartada', fecha_resolucion = now(), motivo_resolucion = 'cancelado por Mercado Pago',
                cancelado_por_mercadopago = true, updated_at = now()
            WHERE p.tipo = 'retiro' AND p.estado = 'pendiente'
              AND EXISTS (
                  SELECT 1 FROM filas_conciliacion_mercadopago f
                  WHERE f.tipo_transaccion = 'WITHDRAWAL_CANCEL' AND f.id_tenant = p.id_tenant
                    AND f.id_empresa = p.id_empresa AND f.source_id = p.clave_origen);
            INSERT INTO propuestas_tesoreria_mercadopago (
                id_tenant, id_empresa, id_conciliacion_mercadopago, tipo, clave_origen, importe, id_fila_conciliacion_mercadopago,
                id_cuenta_origen, id_cuenta_destino, estado, cancelado_por_mercadopago, created_at, updated_at)
            SELECT p.id_tenant, p.id_empresa, c.id_conciliacion_mercadopago, 'retiro_cancelado', p.clave_origen, p.importe,
                   c.id_fila_conciliacion_mercadopago, p.id_cuenta_origen, p.id_cuenta_destino, 'pendiente', true, now(), now()
            FROM propuestas_tesoreria_mercadopago p
            CROSS JOIN LATERAL (
                SELECT f.id_fila_conciliacion_mercadopago, f.id_conciliacion_mercadopago
                FROM filas_conciliacion_mercadopago f
                WHERE f.tipo_transaccion = 'WITHDRAWAL_CANCEL' AND f.id_tenant = p.id_tenant
                  AND f.id_empresa = p.id_empresa AND f.source_id = p.clave_origen
                ORDER BY f.id_fila_conciliacion_mercadopago
                LIMIT 1) c
            WHERE p.tipo = 'retiro' AND p.estado = 'aplicada'
              AND NOT EXISTS (
                  SELECT 1 FROM propuestas_tesoreria_mercadopago r
                  WHERE r.tipo = 'retiro_cancelado' AND r.id_tenant = p.id_tenant
                    AND r.id_empresa = p.id_empresa AND r.clave_origen = p.clave_origen)
            ON CONFLICT DO NOTHING;
            UPDATE propuestas_tesoreria_mercadopago p
            SET cancelado_por_mercadopago = true
            WHERE p.cancelado_por_mercadopago = false
              AND (p.tipo = 'retiro_cancelado'
                   OR (p.estado = 'descartada' AND p.motivo_resolucion = 'cancelado por Mercado Pago')
                   OR (p.tipo = 'retiro' AND (
                       EXISTS (
                           SELECT 1 FROM propuestas_tesoreria_mercadopago r
                           WHERE r.tipo = 'retiro_cancelado' AND r.id_tenant = p.id_tenant
                             AND r.id_empresa = p.id_empresa AND r.clave_origen = p.clave_origen)
                       OR EXISTS (
                           SELECT 1 FROM filas_conciliacion_mercadopago f
                           WHERE f.tipo_transaccion = 'WITHDRAWAL_CANCEL' AND f.id_tenant = p.id_tenant
                             AND f.id_empresa = p.id_empresa AND f.source_id = p.clave_origen))));
            """;

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "ix_filas_conciliacion_mercadopago_cancelaciones",
                table: "filas_conciliacion_mercadopago");

            migrationBuilder.DropColumn(
                name: "cancelado_por_mercadopago",
                table: "propuestas_tesoreria_mercadopago");
        }
    }
}
