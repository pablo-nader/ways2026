using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Ways.Infrastructure.Persistencia.Migraciones
{
    /// <inheritdoc />
    public partial class EdicionDeRetiros : Migration
    {
        /// <summary>Marca los retiros que ya escribió el cierre por retiro: el cierre los inserta con su motivo fijo y con
        /// <c>creado_el</c> igual a la <c>fecha_cierre</c> que el mismo statement le pone al turno, y un retiro manual no puede
        /// coincidir en las dos cosas porque solo se registra con el turno abierto. Idempotente: no toca filas ya marcadas.</summary>
        public const string SqlDelBackfill =
            """
            SET LOCAL app.acceso = 'plataforma';
            -- rls-migration-backfills: sin el SET LOCAL de arriba, ways_app (NOBYPASSRLS) no ve filas y el UPDATE reporta éxito sin hacer nada.

            UPDATE movimientos_caja m SET es_retiro_de_cierre = true
            FROM turnos_caja t
            WHERE t.id_turno_caja = m.id_turno_caja AND t.id_tenant = m.id_tenant
              AND m.tipo = 'retiro' AND m.motivo = 'Retiro de cierre de turno'
              AND t.fecha_cierre IS NOT NULL AND m.creado_el = t.fecha_cierre
              AND NOT m.es_retiro_de_cierre;
            """;

        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<DateTimeOffset>(
                name: "deleted_at",
                table: "movimientos_caja",
                type: "timestamp with time zone",
                nullable: true);

            migrationBuilder.AddColumn<bool>(
                name: "es_retiro_de_cierre",
                table: "movimientos_caja",
                type: "boolean",
                nullable: false,
                defaultValue: false);

            migrationBuilder.Sql(SqlDelBackfill);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "deleted_at",
                table: "movimientos_caja");

            migrationBuilder.DropColumn(
                name: "es_retiro_de_cierre",
                table: "movimientos_caja");
        }
    }
}
