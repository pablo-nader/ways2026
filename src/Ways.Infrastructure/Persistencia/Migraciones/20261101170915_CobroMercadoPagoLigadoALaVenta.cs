using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Ways.Infrastructure.Persistencia.Migraciones
{
    /// <inheritdoc />
    public partial class CobroMercadoPagoLigadoALaVenta : Migration
    {
        public const string SqlDeBajaDeAprobadosSinPago =
            """
            SET LOCAL app.acceso = 'plataforma';
            -- rls-migration-backfills: cobros_mercadopago corre con FORCE ROW LEVEL SECURITY; sin el GUC de plataforma el rol
            -- de la aplicación no ve ninguna fila y el UPDATE no escribe nada.
            UPDATE cobros_mercadopago
            SET estado = 'pendiente', fecha_procesada_mercadopago = COALESCE(fecha_procesada_mercadopago, now())
            WHERE estado = 'aprobado' AND id_pago_mercadopago IS NULL;
            """;

        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<DateTimeOffset>(
                name: "fecha_procesada_mercadopago",
                table: "cobros_mercadopago",
                type: "timestamp with time zone",
                nullable: true);

            // Un cobro aprobado por la etapa anterior sin id de pago vuelve a pendiente: la próxima consulta lo reaprueba si el pago
            // ya es legible, y el CHECK que sigue no falla en una base que ya corrió esa etapa.
            migrationBuilder.Sql(SqlDeBajaDeAprobadosSinPago);

            migrationBuilder.AddCheckConstraint(
                name: "ck_cobros_mercadopago_aprobado_con_pago",
                table: "cobros_mercadopago",
                sql: "estado <> 'aprobado' OR id_pago_mercadopago IS NOT NULL");

            migrationBuilder.AddColumn<int>(
                name: "id_comprobante_venta",
                table: "cobros_mercadopago",
                type: "integer",
                nullable: true);

            migrationBuilder.AddColumn<int>(
                name: "id_cobro_mercadopago",
                table: "cobros",
                type: "integer",
                nullable: true);

            migrationBuilder.AddUniqueConstraint(
                name: "ak_cobros_mercadopago_id_id_tenant",
                table: "cobros_mercadopago",
                columns: new[] { "id_cobro_mercadopago", "id_tenant" });

            migrationBuilder.CreateIndex(
                name: "ux_cobros_mercadopago_comprobante_venta",
                table: "cobros_mercadopago",
                columns: new[] { "id_comprobante_venta", "id_tenant" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "ix_cobros_cobro_mercadopago",
                table: "cobros",
                columns: new[] { "id_cobro_mercadopago", "id_tenant" });

            migrationBuilder.AddForeignKey(
                name: "fk_cobros_cobro_mercadopago",
                table: "cobros",
                columns: new[] { "id_cobro_mercadopago", "id_tenant" },
                principalTable: "cobros_mercadopago",
                principalColumns: new[] { "id_cobro_mercadopago", "id_tenant" },
                onDelete: ReferentialAction.Restrict);

            migrationBuilder.AddForeignKey(
                name: "fk_cobros_mercadopago_comprobante_venta",
                table: "cobros_mercadopago",
                columns: new[] { "id_comprobante_venta", "id_tenant" },
                principalTable: "comprobantes_venta",
                principalColumns: new[] { "id_comprobante_venta", "id_tenant" },
                onDelete: ReferentialAction.Restrict);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropCheckConstraint(
                name: "ck_cobros_mercadopago_aprobado_con_pago",
                table: "cobros_mercadopago");

            migrationBuilder.DropForeignKey(
                name: "fk_cobros_cobro_mercadopago",
                table: "cobros");

            migrationBuilder.DropForeignKey(
                name: "fk_cobros_mercadopago_comprobante_venta",
                table: "cobros_mercadopago");

            migrationBuilder.DropUniqueConstraint(
                name: "ak_cobros_mercadopago_id_id_tenant",
                table: "cobros_mercadopago");

            migrationBuilder.DropIndex(
                name: "ux_cobros_mercadopago_comprobante_venta",
                table: "cobros_mercadopago");

            migrationBuilder.DropIndex(
                name: "ix_cobros_cobro_mercadopago",
                table: "cobros");

            migrationBuilder.DropColumn(
                name: "fecha_procesada_mercadopago",
                table: "cobros_mercadopago");

            migrationBuilder.DropColumn(
                name: "id_comprobante_venta",
                table: "cobros_mercadopago");

            migrationBuilder.DropColumn(
                name: "id_cobro_mercadopago",
                table: "cobros");
        }
    }
}
