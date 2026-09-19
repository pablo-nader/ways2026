using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Ways.Infrastructure.Persistencia.Migraciones
{
    /// <inheritdoc />
    public partial class TurnosCajaMedioPagoEfectivo : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<int>(
                name: "id_medio_pago_efectivo",
                table: "turnos_caja",
                type: "integer",
                nullable: true);

            migrationBuilder.CreateIndex(
                name: "ix_turnos_caja_medio_pago_efectivo",
                table: "turnos_caja",
                columns: new[] { "id_medio_pago_efectivo", "id_tenant" });

            migrationBuilder.AddCheckConstraint(
                name: "ck_turnos_caja_medio_efectivo_solo_cerrado",
                table: "turnos_caja",
                sql: "id_medio_pago_efectivo IS NULL OR estado = 'cerrado'");

            migrationBuilder.AddForeignKey(
                name: "fk_turnos_caja_medio_pago_efectivo",
                table: "turnos_caja",
                columns: new[] { "id_medio_pago_efectivo", "id_tenant" },
                principalTable: "medios_pago",
                principalColumns: new[] { "id_medio_pago", "id_tenant" },
                onDelete: ReferentialAction.Restrict);

            // judgment-day JD-E5a-2 (DB CHANGE GATE aprobado): backfill de los turnos YA
            // cerrados — mismo criterio de resolución que ResolvedorDeMedioDeCajaFisica.Resolver
            // (TODAS las filas del catálogo, sin filtrar activo): el ancla es el único medio del
            // tenant con comportamiento = efectivo cuando existe exactamente uno; si el tenant
            // tiene cero o más de uno (catálogo mal configurado, o editado después del cierre),
            // el turno queda NULL a propósito — "fail-closed, nunca adivinar", mismo criterio que
            // el backfill de id_remito (docs/10 §Stock). Corre DESPUÉS de la CHECK/FK de arriba:
            // solo toca turnos con estado = 'cerrado' (satisface la CHECK) y solo asigna ids que
            // ya existen en medios_pago (satisface la FK) — nunca puede violar ninguna de las dos.
            migrationBuilder.Sql(
                """
                UPDATE turnos_caja t
                SET id_medio_pago_efectivo = ancla.id_medio_pago
                FROM (
                    SELECT id_tenant, MIN(id_medio_pago) AS id_medio_pago
                    FROM medios_pago
                    WHERE comportamiento = 'efectivo'
                    GROUP BY id_tenant
                    HAVING COUNT(*) = 1
                ) AS ancla
                WHERE t.id_tenant = ancla.id_tenant
                  AND t.estado = 'cerrado';
                """);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropForeignKey(
                name: "fk_turnos_caja_medio_pago_efectivo",
                table: "turnos_caja");

            migrationBuilder.DropIndex(
                name: "ix_turnos_caja_medio_pago_efectivo",
                table: "turnos_caja");

            migrationBuilder.DropCheckConstraint(
                name: "ck_turnos_caja_medio_efectivo_solo_cerrado",
                table: "turnos_caja");

            migrationBuilder.DropColumn(
                name: "id_medio_pago_efectivo",
                table: "turnos_caja");
        }
    }
}
