using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Ways.Infrastructure.Persistencia.Migraciones
{
    /// <inheritdoc />
    public partial class CobroMercadoPagoPoint : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropCheckConstraint(
                name: "ck_cobros_mercadopago_estado",
                table: "cobros_mercadopago");

            migrationBuilder.DropCheckConstraint(
                name: "ck_cobros_mercadopago_orden",
                table: "cobros_mercadopago");

            migrationBuilder.AddColumn<string>(
                name: "id_terminal_point",
                table: "cobros_mercadopago",
                type: "character varying(80)",
                maxLength: 80,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "situacion_terminal",
                table: "cobros_mercadopago",
                type: "character varying(20)",
                maxLength: 20,
                nullable: true);

            migrationBuilder.AddCheckConstraint(
                name: "ck_cobros_mercadopago_estado",
                table: "cobros_mercadopago",
                sql: "estado IN ('creando', 'pendiente', 'aprobado', 'cancelado', 'expirado', 'devuelto', 'sin_venta', 'fallido')");

            migrationBuilder.AddCheckConstraint(
                name: "ck_cobros_mercadopago_orden",
                table: "cobros_mercadopago",
                sql: "estado IN ('creando', 'expirado') OR id_orden_mercadopago IS NOT NULL OR (estado = 'cancelado' AND tipo = 'point')");

            migrationBuilder.AddCheckConstraint(
                name: "ck_cobros_mercadopago_situacion_terminal",
                table: "cobros_mercadopago",
                sql: "situacion_terminal IS NULL OR (tipo = 'point' AND estado = 'pendiente' AND situacion_terminal IN ('en_terminal', 'revisar_terminal'))");

            migrationBuilder.AddCheckConstraint(
                name: "ck_cobros_mercadopago_terminal",
                table: "cobros_mercadopago",
                sql: "(tipo = 'point') = (id_terminal_point IS NOT NULL)");
        }

        /// <summary>Bloque de la vuelta atrás, con el GUC de plataforma (rls-migration-backfills): sin él el rol de la aplicación no ve
        /// ninguna fila.</summary>
        public const string SqlDeVueltaDeFallido =
            """
            SET LOCAL app.acceso = 'plataforma';
            UPDATE cobros_mercadopago SET estado = 'cancelado', situacion_terminal = NULL WHERE estado = 'fallido';
            UPDATE cobros_mercadopago SET estado = 'expirado' WHERE estado = 'cancelado' AND id_orden_mercadopago IS NULL;
            """;

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropCheckConstraint(
                name: "ck_cobros_mercadopago_estado",
                table: "cobros_mercadopago");

            migrationBuilder.DropCheckConstraint(
                name: "ck_cobros_mercadopago_orden",
                table: "cobros_mercadopago");

            migrationBuilder.DropCheckConstraint(
                name: "ck_cobros_mercadopago_situacion_terminal",
                table: "cobros_mercadopago");

            migrationBuilder.DropCheckConstraint(
                name: "ck_cobros_mercadopago_terminal",
                table: "cobros_mercadopago");

            // Un cobro rechazado en la terminal vuelve a cancelado (el CHECK anterior no conoce fallido) y un Point cancelado sin orden
            // (terminal ocupada al crear) vuelve a expirado, el único estado final sin orden que el CHECK anterior admite.
            migrationBuilder.Sql(SqlDeVueltaDeFallido);

            migrationBuilder.DropColumn(
                name: "id_terminal_point",
                table: "cobros_mercadopago");

            migrationBuilder.DropColumn(
                name: "situacion_terminal",
                table: "cobros_mercadopago");

            migrationBuilder.AddCheckConstraint(
                name: "ck_cobros_mercadopago_estado",
                table: "cobros_mercadopago",
                sql: "estado IN ('creando', 'pendiente', 'aprobado', 'cancelado', 'expirado', 'devuelto', 'sin_venta')");

            migrationBuilder.AddCheckConstraint(
                name: "ck_cobros_mercadopago_orden",
                table: "cobros_mercadopago",
                sql: "estado IN ('creando', 'expirado') OR id_orden_mercadopago IS NOT NULL");
        }
    }
}
