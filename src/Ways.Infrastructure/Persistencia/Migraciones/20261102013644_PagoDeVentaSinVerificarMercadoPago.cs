using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Ways.Infrastructure.Persistencia.Migraciones
{
    /// <inheritdoc />
    public partial class PagoDeVentaSinVerificarMercadoPago : Migration
    {
        public const string SqlDeVerificacionDeLosPagosCobrados =
            """
            SET LOCAL app.acceso = 'plataforma';
            -- rls-migration-backfills: cobros corre con FORCE ROW LEVEL SECURITY; sin el GUC de plataforma el rol de la aplicación no ve
            -- ninguna fila y el UPDATE no escribe nada.
            UPDATE cobros
            SET verificacion_mercadopago = 'mercadopago'
            WHERE id_cobro_mercadopago IS NOT NULL AND verificacion_mercadopago IS NULL;
            """;

        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "verificacion_mercadopago",
                table: "cobros",
                type: "character varying(20)",
                maxLength: 20,
                nullable: true);

            migrationBuilder.CreateIndex(
                name: "ix_cobros_sin_verificar",
                table: "cobros",
                columns: new[] { "id_tenant", "id_comprobante_venta" },
                filter: "verificacion_mercadopago = 'sin_verificar' AND deleted_at IS NULL");

            // Los pagos que ya se apoyaban en un cobro aprobado quedan marcados antes de que el CHECK de coherencia los juzgue.
            migrationBuilder.Sql(SqlDeVerificacionDeLosPagosCobrados);

            migrationBuilder.AddCheckConstraint(
                name: "ck_cobros_verificacion_mercadopago",
                table: "cobros",
                sql: "verificacion_mercadopago IS NULL OR verificacion_mercadopago IN ('mercadopago', 'sin_verificar', 'verificado_manualmente')");

            migrationBuilder.AddCheckConstraint(
                name: "ck_cobros_verificacion_mercadopago_cobro",
                table: "cobros",
                sql: "(id_cobro_mercadopago IS NOT NULL) = (verificacion_mercadopago IS NOT DISTINCT FROM 'mercadopago')");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "ix_cobros_sin_verificar",
                table: "cobros");

            migrationBuilder.DropCheckConstraint(
                name: "ck_cobros_verificacion_mercadopago",
                table: "cobros");

            migrationBuilder.DropCheckConstraint(
                name: "ck_cobros_verificacion_mercadopago_cobro",
                table: "cobros");

            migrationBuilder.DropColumn(
                name: "verificacion_mercadopago",
                table: "cobros");
        }
    }
}
