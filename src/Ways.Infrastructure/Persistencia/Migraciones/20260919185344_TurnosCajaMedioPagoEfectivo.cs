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

            // Backfill de los turnos ya cerrados con el mismo criterio que
            // ResolvedorDeMedioDeCajaFisica.Resolver: el ancla es el único medio del tenant con
            // comportamiento = efectivo; con cero o más de uno el turno queda NULL (nunca se
            // adivina). Corre después de la CHECK y la FK, así que no puede violarlas.
            //
            // turnos_caja corre bajo FORCE ROW LEVEL SECURITY y el rol de aplicación no tiene
            // BYPASSRLS: `dotnet ef database update` (WaysDbContextFactory, sin interceptor de
            // tenant) no vería ninguna fila y reportaría éxito. Por eso el SET LOCAL va dentro del
            // mismo bloque Sql(), igual que en CostoCongeladoEnVentaEtapa9 y QuitarVueltoMaximo
            // (skill rls-migration-backfills).
            migrationBuilder.Sql(
                """
                SET LOCAL app.acceso = 'plataforma';

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
