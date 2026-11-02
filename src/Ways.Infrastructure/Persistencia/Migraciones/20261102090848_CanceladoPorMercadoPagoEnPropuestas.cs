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

            migrationBuilder.Sql(SqlDeBackfill);
        }

        /// <summary>Marca las propuestas que una cancelación ya había tocado: las reversiones, los retiros descartados por la cancelación y los retiros
        /// aplicados que tienen su reversión. Con el GUC de plataforma (rls-migration-backfills): sin él el rol de la aplicación no ve ninguna fila. Es
        /// idempotente: salta las ya marcadas.</summary>
        public const string SqlDeBackfill =
            """
            SET LOCAL app.acceso = 'plataforma';
            UPDATE propuestas_tesoreria_mercadopago p
            SET cancelado_por_mercadopago = true
            WHERE p.cancelado_por_mercadopago = false
              AND (p.tipo = 'retiro_cancelado'
                   OR (p.estado = 'descartada' AND p.motivo_resolucion = 'cancelado por Mercado Pago')
                   OR (p.tipo = 'retiro' AND EXISTS (
                       SELECT 1 FROM propuestas_tesoreria_mercadopago r
                       WHERE r.tipo = 'retiro_cancelado' AND r.id_tenant = p.id_tenant
                         AND r.id_empresa = p.id_empresa AND r.clave_origen = p.clave_origen)));
            """;

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "cancelado_por_mercadopago",
                table: "propuestas_tesoreria_mercadopago");
        }
    }
}
