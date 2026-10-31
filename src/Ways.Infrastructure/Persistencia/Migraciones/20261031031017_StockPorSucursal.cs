using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Ways.Infrastructure.Persistencia.Migraciones
{
    /// <inheritdoc />
    public partial class StockPorSucursal : Migration
    {
        /// <summary>Cada fila de <c>stock</c>, <c>stock_lotes</c> y <c>movimientos_stock</c> toma la sucursal de su punto de
        /// venta (y la transferencia, la del punto de venta de destino). Idempotente: solo toca las filas todavía sin
        /// sucursal. Antes de cambiar las claves verifica que ninguna fila de stock caiga sobre otra: si dos puntos de venta
        /// con stock propio ya compartieran sucursal, la migración aborta en vez de fusionar saldos en silencio.</summary>
        public const string SqlDelBackfill =
            """
            SET LOCAL app.acceso = 'plataforma';
            -- rls-migration-backfills: sin el SET LOCAL de arriba, ways_app (NOBYPASSRLS) no ve filas y los UPDATE reportan éxito sin hacer nada.

            UPDATE stock s SET id_sucursal = pv.id_sucursal
            FROM puntos_venta pv
            WHERE pv.id_punto_venta = s.id_punto_venta AND s.id_sucursal IS NULL;

            UPDATE stock_lotes s SET id_sucursal = pv.id_sucursal
            FROM puntos_venta pv
            WHERE pv.id_punto_venta = s.id_punto_venta AND s.id_sucursal IS NULL;

            UPDATE movimientos_stock m SET id_sucursal = pv.id_sucursal
            FROM puntos_venta pv
            WHERE pv.id_punto_venta = m.id_punto_venta AND m.id_sucursal IS NULL;

            UPDATE movimientos_stock m SET id_sucursal_destino = pv.id_sucursal
            FROM puntos_venta pv
            WHERE pv.id_punto_venta = m.id_punto_venta_destino AND m.id_sucursal_destino IS NULL;

            DO $control$
            BEGIN
                IF EXISTS (
                    SELECT 1 FROM stock WHERE id_sucursal IS NOT NULL
                    GROUP BY id_articulo, id_sucursal HAVING count(*) > 1)
                THEN
                    RAISE EXCEPTION 'stock_por_sucursal: dos filas de stock quedarían con el mismo (id_articulo, id_sucursal)';
                END IF;

                IF EXISTS (
                    SELECT 1 FROM stock_lotes WHERE id_sucursal IS NOT NULL
                    GROUP BY id_articulo, id_sucursal, id_lote HAVING count(*) > 1)
                THEN
                    RAISE EXCEPTION 'stock_por_sucursal: dos filas de stock_lotes quedarían con el mismo (id_articulo, id_sucursal, id_lote)';
                END IF;
            END
            $control$;
            """;

        /// <summary>Reversa: cada fila de <c>stock</c> y <c>stock_lotes</c> vuelve al punto de venta de menor id de su
        /// sucursal. Una sucursal tiene una sola fila por clave y cada punto de venta es de una sola sucursal, así que
        /// ninguna fila cae sobre otra.</summary>
        public const string SqlDeLaReversa =
            """
            SET LOCAL app.acceso = 'plataforma';
            -- rls-migration-backfills: sin el SET LOCAL de arriba, ways_app (NOBYPASSRLS) no ve filas y los UPDATE reportan éxito sin hacer nada.

            UPDATE stock s SET id_punto_venta = (
                SELECT min(pv.id_punto_venta) FROM puntos_venta pv WHERE pv.id_sucursal = s.id_sucursal)
            WHERE s.id_punto_venta IS NULL;

            UPDATE stock_lotes s SET id_punto_venta = (
                SELECT min(pv.id_punto_venta) FROM puntos_venta pv WHERE pv.id_sucursal = s.id_sucursal)
            WHERE s.id_punto_venta IS NULL;
            """;

        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<int>(
                name: "id_sucursal",
                table: "stock",
                type: "integer",
                nullable: true);

            migrationBuilder.AddColumn<int>(
                name: "id_sucursal",
                table: "stock_lotes",
                type: "integer",
                nullable: true);

            migrationBuilder.AddColumn<int>(
                name: "id_sucursal",
                table: "movimientos_stock",
                type: "integer",
                nullable: true);

            migrationBuilder.AddColumn<int>(
                name: "id_sucursal_destino",
                table: "movimientos_stock",
                type: "integer",
                nullable: true);

            migrationBuilder.Sql(SqlDelBackfill);

            foreach (var tabla in new[] { "stock", "stock_lotes", "movimientos_stock" })
            {
                migrationBuilder.AlterColumn<int>(
                    name: "id_sucursal",
                    table: tabla,
                    type: "integer",
                    nullable: false,
                    oldClrType: typeof(int),
                    oldType: "integer",
                    oldNullable: true);
            }

            // stock: la clave pasa del punto de venta a la sucursal.
            migrationBuilder.DropForeignKey(name: "fk_stock_punto_venta", table: "stock");
            migrationBuilder.DropPrimaryKey(name: "pk_stock", table: "stock");
            migrationBuilder.DropIndex(name: "ix_stock_punto_venta", table: "stock");
            migrationBuilder.DropColumn(name: "id_punto_venta", table: "stock");

            migrationBuilder.AddPrimaryKey(name: "pk_stock", table: "stock", columns: new[] { "id_articulo", "id_sucursal" });
            migrationBuilder.CreateIndex(
                name: "ix_stock_sucursal",
                table: "stock",
                columns: new[] { "id_sucursal", "id_tenant" });
            migrationBuilder.AddForeignKey(
                name: "fk_stock_sucursal",
                table: "stock",
                columns: new[] { "id_sucursal", "id_tenant" },
                principalTable: "sucursales",
                principalColumns: new[] { "id_sucursal", "id_tenant" },
                onDelete: ReferentialAction.Restrict);

            // stock_lotes: ídem, con el lote como tercera columna de la clave.
            migrationBuilder.DropForeignKey(name: "fk_stock_lotes_punto_venta", table: "stock_lotes");
            migrationBuilder.DropPrimaryKey(name: "pk_stock_lotes", table: "stock_lotes");
            migrationBuilder.DropIndex(name: "ix_stock_lotes_punto_venta", table: "stock_lotes");
            migrationBuilder.DropColumn(name: "id_punto_venta", table: "stock_lotes");

            migrationBuilder.AddPrimaryKey(
                name: "pk_stock_lotes",
                table: "stock_lotes",
                columns: new[] { "id_articulo", "id_sucursal", "id_lote" });
            migrationBuilder.CreateIndex(
                name: "ix_stock_lotes_sucursal",
                table: "stock_lotes",
                columns: new[] { "id_sucursal", "id_tenant" });
            migrationBuilder.AddForeignKey(
                name: "fk_stock_lotes_sucursal",
                table: "stock_lotes",
                columns: new[] { "id_sucursal", "id_tenant" },
                principalTable: "sucursales",
                principalColumns: new[] { "id_sucursal", "id_tenant" },
                onDelete: ReferentialAction.Restrict);

            // movimientos_stock: conserva la caja de origen y gana la sucursal cuyo saldo movió.
            migrationBuilder.DropIndex(name: "ix_movimientos_stock_articulo_punto_venta", table: "movimientos_stock");

            migrationBuilder.CreateIndex(
                name: "ix_movimientos_stock_articulo_sucursal",
                table: "movimientos_stock",
                columns: new[] { "id_articulo", "id_sucursal", "id_tenant" });
            migrationBuilder.CreateIndex(
                name: "ix_movimientos_stock_sucursal",
                table: "movimientos_stock",
                columns: new[] { "id_sucursal", "id_tenant" });
            migrationBuilder.CreateIndex(
                name: "ix_movimientos_stock_sucursal_destino",
                table: "movimientos_stock",
                columns: new[] { "id_sucursal_destino", "id_tenant" });

            migrationBuilder.AddForeignKey(
                name: "fk_movimientos_stock_sucursal",
                table: "movimientos_stock",
                columns: new[] { "id_sucursal", "id_tenant" },
                principalTable: "sucursales",
                principalColumns: new[] { "id_sucursal", "id_tenant" },
                onDelete: ReferentialAction.Restrict);
            migrationBuilder.AddForeignKey(
                name: "fk_movimientos_stock_sucursal_destino",
                table: "movimientos_stock",
                columns: new[] { "id_sucursal_destino", "id_tenant" },
                principalTable: "sucursales",
                principalColumns: new[] { "id_sucursal", "id_tenant" },
                onDelete: ReferentialAction.Restrict);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropForeignKey(name: "fk_movimientos_stock_sucursal", table: "movimientos_stock");
            migrationBuilder.DropForeignKey(name: "fk_movimientos_stock_sucursal_destino", table: "movimientos_stock");
            migrationBuilder.DropIndex(name: "ix_movimientos_stock_articulo_sucursal", table: "movimientos_stock");
            migrationBuilder.DropIndex(name: "ix_movimientos_stock_sucursal", table: "movimientos_stock");
            migrationBuilder.DropIndex(name: "ix_movimientos_stock_sucursal_destino", table: "movimientos_stock");
            migrationBuilder.DropColumn(name: "id_sucursal", table: "movimientos_stock");
            migrationBuilder.DropColumn(name: "id_sucursal_destino", table: "movimientos_stock");
            migrationBuilder.CreateIndex(
                name: "ix_movimientos_stock_articulo_punto_venta",
                table: "movimientos_stock",
                columns: new[] { "id_articulo", "id_punto_venta", "id_tenant" });

            migrationBuilder.AddColumn<int>(name: "id_punto_venta", table: "stock", type: "integer", nullable: true);
            migrationBuilder.AddColumn<int>(name: "id_punto_venta", table: "stock_lotes", type: "integer", nullable: true);

            migrationBuilder.Sql(SqlDeLaReversa);

            foreach (var tabla in new[] { "stock", "stock_lotes" })
            {
                migrationBuilder.AlterColumn<int>(
                    name: "id_punto_venta",
                    table: tabla,
                    type: "integer",
                    nullable: false,
                    oldClrType: typeof(int),
                    oldType: "integer",
                    oldNullable: true);
            }

            migrationBuilder.DropForeignKey(name: "fk_stock_sucursal", table: "stock");
            migrationBuilder.DropPrimaryKey(name: "pk_stock", table: "stock");
            migrationBuilder.DropIndex(name: "ix_stock_sucursal", table: "stock");
            migrationBuilder.DropColumn(name: "id_sucursal", table: "stock");
            migrationBuilder.AddPrimaryKey(name: "pk_stock", table: "stock", columns: new[] { "id_articulo", "id_punto_venta" });
            migrationBuilder.CreateIndex(
                name: "ix_stock_punto_venta",
                table: "stock",
                columns: new[] { "id_punto_venta", "id_tenant" });
            migrationBuilder.AddForeignKey(
                name: "fk_stock_punto_venta",
                table: "stock",
                columns: new[] { "id_punto_venta", "id_tenant" },
                principalTable: "puntos_venta",
                principalColumns: new[] { "id_punto_venta", "id_tenant" },
                onDelete: ReferentialAction.Restrict);

            migrationBuilder.DropForeignKey(name: "fk_stock_lotes_sucursal", table: "stock_lotes");
            migrationBuilder.DropPrimaryKey(name: "pk_stock_lotes", table: "stock_lotes");
            migrationBuilder.DropIndex(name: "ix_stock_lotes_sucursal", table: "stock_lotes");
            migrationBuilder.DropColumn(name: "id_sucursal", table: "stock_lotes");
            migrationBuilder.AddPrimaryKey(
                name: "pk_stock_lotes",
                table: "stock_lotes",
                columns: new[] { "id_articulo", "id_punto_venta", "id_lote" });
            migrationBuilder.CreateIndex(
                name: "ix_stock_lotes_punto_venta",
                table: "stock_lotes",
                columns: new[] { "id_punto_venta", "id_tenant" });
            migrationBuilder.AddForeignKey(
                name: "fk_stock_lotes_punto_venta",
                table: "stock_lotes",
                columns: new[] { "id_punto_venta", "id_tenant" },
                principalTable: "puntos_venta",
                principalColumns: new[] { "id_punto_venta", "id_tenant" },
                onDelete: ReferentialAction.Restrict);
        }
    }
}
