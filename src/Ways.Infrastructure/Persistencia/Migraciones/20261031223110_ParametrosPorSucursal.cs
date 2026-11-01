using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Ways.Infrastructure.Persistencia.Migraciones
{
    /// <inheritdoc />
    public partial class ParametrosPorSucursal : Migration
    {
        /// <summary>Las cinco claves que pasan a ser de la sucursal (zona horaria, lotes y días de alerta): por cada
        /// sucursal viva y clave, copia el valor de la caja viva de menor id que tenga fila y después da de baja toda fila
        /// por punto de venta de esas claves, que dejan de contar. Con el backfill 1:1 de las sucursales conserva cada
        /// valor efectivo. Idempotente: no copia donde la sucursal ya tiene su fila y, tras la primera corrida, no quedan
        /// filas por punto de venta que copiar.</summary>
        public const string SqlDelBackfill =
            """
            SET LOCAL app.acceso = 'plataforma';
            -- rls-migration-backfills: sin el SET LOCAL de arriba, ways_app (NOBYPASSRLS) no ve filas y los cambios reportan éxito sin hacer nada.

            INSERT INTO parametros (id_tenant, id_empresa, id_sucursal, clave, valor, created_at, updated_at)
            SELECT DISTINCT ON (pv.id_sucursal, p.clave)
                   p.id_tenant, pv.id_empresa, pv.id_sucursal, p.clave, p.valor, now(), now()
            FROM parametros p
            JOIN puntos_venta pv
              ON pv.id_punto_venta = p.id_punto_venta AND pv.id_tenant = p.id_tenant AND pv.id_empresa = p.id_empresa
            JOIN sucursales su
              ON su.id_sucursal = pv.id_sucursal AND su.id_tenant = pv.id_tenant
            WHERE p.deleted_at IS NULL AND pv.deleted_at IS NULL AND su.deleted_at IS NULL
              AND p.clave IN ('zona_horaria', 'lotes_habilitado', 'dias_alerta_vencimiento', 'dias_rotacion', 'dias_cobertura_objetivo')
              AND NOT EXISTS (
                  SELECT 1 FROM parametros q
                  WHERE q.id_sucursal = pv.id_sucursal AND q.clave = p.clave AND q.deleted_at IS NULL)
            ORDER BY pv.id_sucursal, p.clave, pv.id_punto_venta;

            UPDATE parametros SET deleted_at = now(), updated_at = now()
            WHERE id_punto_venta IS NOT NULL AND deleted_at IS NULL
              AND clave IN ('zona_horaria', 'lotes_habilitado', 'dias_alerta_vencimiento', 'dias_rotacion', 'dias_cobertura_objetivo');
            """;

        /// <summary>Reversa: cada valor de sucursal se copia a todas las cajas vivas de la sucursal que no tengan fila
        /// propia y las filas de sucursal se dan de baja (no se borran) antes de quitar la columna.</summary>
        public const string SqlDeLaReversa =
            """
            SET LOCAL app.acceso = 'plataforma';
            -- rls-migration-backfills: sin el SET LOCAL de arriba, ways_app (NOBYPASSRLS) no ve filas y los cambios reportan éxito sin hacer nada.

            INSERT INTO parametros (id_tenant, id_empresa, id_punto_venta, clave, valor, created_at, updated_at)
            SELECT p.id_tenant, p.id_empresa, pv.id_punto_venta, p.clave, p.valor, now(), now()
            FROM parametros p
            JOIN puntos_venta pv ON pv.id_sucursal = p.id_sucursal AND pv.id_tenant = p.id_tenant AND pv.deleted_at IS NULL
            WHERE p.id_sucursal IS NOT NULL AND p.deleted_at IS NULL
              AND NOT EXISTS (
                  SELECT 1 FROM parametros q
                  WHERE q.id_punto_venta = pv.id_punto_venta AND q.clave = p.clave AND q.deleted_at IS NULL);

            UPDATE parametros SET deleted_at = now(), updated_at = now()
            WHERE id_sucursal IS NOT NULL AND deleted_at IS NULL;
            """;

        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "ux_parametros_empresa",
                table: "parametros");

            migrationBuilder.AddColumn<int>(
                name: "id_sucursal",
                table: "parametros",
                type: "integer",
                nullable: true);

            migrationBuilder.CreateIndex(
                name: "ix_parametros_sucursal",
                table: "parametros",
                columns: new[] { "id_sucursal", "id_empresa", "id_tenant" });

            migrationBuilder.CreateIndex(
                name: "ux_parametros_empresa",
                table: "parametros",
                columns: new[] { "id_tenant", "id_empresa", "clave" },
                unique: true,
                filter: "id_punto_venta IS NULL AND id_sucursal IS NULL AND deleted_at IS NULL");

            migrationBuilder.CreateIndex(
                name: "ux_parametros_sucursal",
                table: "parametros",
                columns: new[] { "id_tenant", "id_empresa", "id_sucursal", "clave" },
                unique: true,
                filter: "id_sucursal IS NOT NULL AND deleted_at IS NULL");

            migrationBuilder.AddCheckConstraint(
                name: "ck_parametros_un_solo_alcance",
                table: "parametros",
                sql: "id_punto_venta IS NULL OR id_sucursal IS NULL");

            migrationBuilder.AddForeignKey(
                name: "fk_parametros_sucursal",
                table: "parametros",
                columns: new[] { "id_sucursal", "id_empresa", "id_tenant" },
                principalTable: "sucursales",
                principalColumns: new[] { "id_sucursal", "id_empresa", "id_tenant" },
                onDelete: ReferentialAction.Restrict);

            migrationBuilder.Sql(SqlDelBackfill);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql(SqlDeLaReversa);

            migrationBuilder.DropForeignKey(
                name: "fk_parametros_sucursal",
                table: "parametros");

            migrationBuilder.DropIndex(
                name: "ix_parametros_sucursal",
                table: "parametros");

            migrationBuilder.DropIndex(
                name: "ux_parametros_empresa",
                table: "parametros");

            migrationBuilder.DropIndex(
                name: "ux_parametros_sucursal",
                table: "parametros");

            migrationBuilder.DropCheckConstraint(
                name: "ck_parametros_un_solo_alcance",
                table: "parametros");

            migrationBuilder.DropColumn(
                name: "id_sucursal",
                table: "parametros");

            migrationBuilder.CreateIndex(
                name: "ux_parametros_empresa",
                table: "parametros",
                columns: new[] { "id_tenant", "id_empresa", "clave" },
                unique: true,
                filter: "id_punto_venta IS NULL AND deleted_at IS NULL");
        }
    }
}
