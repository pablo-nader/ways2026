using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Ways.Infrastructure.Persistencia.Migraciones
{
    /// <inheritdoc />
    public partial class RendicionSaldadaEnReservaNumeracion : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<DateTimeOffset>(
                name: "rendicion_saldada_at",
                table: "reservas_numeracion",
                type: "timestamp with time zone",
                nullable: true);

            // Backfill OBLIGATORIO, no cosmético: la guarda de cierre dejó de excluir los bloques
            // abandonados (antes rotar el bloque borraba el hueco solo), así que sin esto cada bloque
            // abandonado histórico entra en su alcance con reportado_at NULL, cae en SinReporte y
            // bloquea para siempre todos los cierres de su punto de venta. Son bloques anteriores a
            // la guarda y nadie puede rendirlos: se dan por saldados de una vez.
            //
            // reservas_numeracion corre bajo FORCE ROW LEVEL SECURITY y el rol de aplicación no tiene
            // BYPASSRLS: `dotnet ef database update` (WaysDbContextFactory, sin interceptor de
            // tenant) no vería ninguna fila y reportaría éxito. Por eso el SET LOCAL va dentro del
            // mismo bloque Sql(), igual que en TurnosCajaMedioPagoEfectivo y CostoCongeladoEnVentaEtapa9
            // (skill rls-migration-backfills). El WHERE excluye lo ya saldado, así que es idempotente.
            //
            // CUIDADO con rodar Down y volver a aplicar Up (judgment-day): Down DROPEA la columna, así
            // que el segundo Up vuelve a saldar TODOS los bloques abandonados, incluidos los
            // posteriores al despliegue que estaban bloqueando de verdad con ventas sin drenar — su
            // bloqueo desaparecería en silencio. El backfill es correcto UNA vez, contra los bloques
            // históricos anteriores a la guarda; un rollback casual de esta migración no es una
            // operación sin consecuencias.
            migrationBuilder.Sql(
                """
                SET LOCAL app.acceso = 'plataforma';

                UPDATE reservas_numeracion
                SET rendicion_saldada_at = now(), updated_at = now()
                WHERE abandonada_at IS NOT NULL
                  AND rendicion_saldada_at IS NULL;
                """);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "rendicion_saldada_at",
                table: "reservas_numeracion");
        }
    }
}
