using System;
using Microsoft.EntityFrameworkCore.Migrations;
using Npgsql.EntityFrameworkCore.PostgreSQL.Metadata;
using Ways.Infrastructure.Multitenancy;

#nullable disable

namespace Ways.Infrastructure.Persistencia.Migraciones
{
    /// <inheritdoc />
    public partial class Sucursales : Migration
    {
        /// <summary>Una sucursal por cada punto de venta existente (1:1): mismo tenant, misma empresa, mismo nombre y las
        /// mismas marcas de tiempo, incluida la baja lógica, y el punto de venta queda apuntando a la suya. Idempotente: solo
        /// toma los puntos de venta que todavía no tienen sucursal. El bloque <c>DO</c> mapea cada identidad generada con su
        /// punto de venta sin una columna auxiliar.</summary>
        public const string SqlDelBackfill =
            """
            SET LOCAL app.acceso = 'plataforma';
            -- rls-migration-backfills: sin el SET LOCAL de arriba, ways_app (NOBYPASSRLS) no ve filas y el bloque reporta éxito sin hacer nada.

            DO $backfill$
            DECLARE
                pv RECORD;
                v_id_sucursal integer;
            BEGIN
                FOR pv IN
                    SELECT id_punto_venta, id_tenant, id_empresa, nombre, created_at, updated_at, deleted_at
                    FROM puntos_venta
                    WHERE id_sucursal IS NULL
                    ORDER BY id_punto_venta
                LOOP
                    INSERT INTO sucursales (id_tenant, id_empresa, nombre, created_at, updated_at, deleted_at)
                    VALUES (pv.id_tenant, pv.id_empresa, pv.nombre, pv.created_at, pv.updated_at, pv.deleted_at)
                    RETURNING id_sucursal INTO v_id_sucursal;

                    UPDATE puntos_venta SET id_sucursal = v_id_sucursal WHERE id_punto_venta = pv.id_punto_venta;
                END LOOP;
            END
            $backfill$;
            """;

        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<int>(
                name: "id_sucursal",
                table: "puntos_venta",
                type: "integer",
                nullable: true);

            migrationBuilder.CreateTable(
                name: "sucursales",
                columns: table => new
                {
                    id_sucursal = table.Column<int>(type: "integer", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    id_empresa = table.Column<int>(type: "integer", nullable: false),
                    nombre = table.Column<string>(type: "citext", maxLength: 150, nullable: false),
                    created_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    updated_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    deleted_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    id_tenant = table.Column<int>(type: "integer", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_sucursales", x => x.id_sucursal);
                    table.UniqueConstraint("ak_sucursales_id_sucursal_id_empresa_id_tenant", x => new { x.id_sucursal, x.id_empresa, x.id_tenant });
                    table.UniqueConstraint("ak_sucursales_id_sucursal_id_tenant", x => new { x.id_sucursal, x.id_tenant });
                    table.ForeignKey(
                        name: "fk_sucursales_empresa",
                        columns: x => new { x.id_empresa, x.id_tenant },
                        principalTable: "empresas",
                        principalColumns: new[] { "id_empresa", "id_tenant" },
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateIndex(
                name: "ix_sucursales_empresa",
                table: "sucursales",
                columns: new[] { "id_empresa", "id_tenant" });

            migrationBuilder.CreateIndex(
                name: "ix_sucursales_tenant",
                table: "sucursales",
                column: "id_tenant");

            migrationBuilder.HabilitarRlsDeTenant("sucursales");

            migrationBuilder.Sql(SqlDelBackfill);

            migrationBuilder.AlterColumn<int>(
                name: "id_sucursal",
                table: "puntos_venta",
                type: "integer",
                nullable: false,
                oldClrType: typeof(int),
                oldType: "integer",
                oldNullable: true);

            migrationBuilder.CreateIndex(
                name: "IX_puntos_venta_id_sucursal_id_empresa_id_tenant",
                table: "puntos_venta",
                columns: new[] { "id_sucursal", "id_empresa", "id_tenant" });

            migrationBuilder.AddForeignKey(
                name: "fk_puntos_venta_sucursal",
                table: "puntos_venta",
                columns: new[] { "id_sucursal", "id_empresa", "id_tenant" },
                principalTable: "sucursales",
                principalColumns: new[] { "id_sucursal", "id_empresa", "id_tenant" },
                onDelete: ReferentialAction.Restrict);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropForeignKey(
                name: "fk_puntos_venta_sucursal",
                table: "puntos_venta");

            migrationBuilder.DropIndex(
                name: "IX_puntos_venta_id_sucursal_id_empresa_id_tenant",
                table: "puntos_venta");

            migrationBuilder.DropColumn(
                name: "id_sucursal",
                table: "puntos_venta");

            migrationBuilder.DropTable(
                name: "sucursales");
        }
    }
}
