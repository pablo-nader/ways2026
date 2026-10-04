using System;
using Microsoft.EntityFrameworkCore.Migrations;
using Npgsql.EntityFrameworkCore.PostgreSQL.Metadata;
using Ways.Infrastructure.Multitenancy;

#nullable disable

namespace Ways.Infrastructure.Persistencia.Migraciones
{
    /// <inheritdoc />
    public partial class FamiliasDeArticulos : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<int>(
                name: "id_familia",
                table: "articulos",
                type: "integer",
                nullable: true);

            migrationBuilder.CreateTable(
                name: "familias",
                columns: table => new
                {
                    id_familia = table.Column<int>(type: "integer", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    nombre = table.Column<string>(type: "citext", maxLength: 150, nullable: false),
                    activo = table.Column<bool>(type: "boolean", nullable: false, defaultValue: true),
                    created_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    updated_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    deleted_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    id_tenant = table.Column<int>(type: "integer", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_familias", x => x.id_familia);
                    table.UniqueConstraint("ak_familias_id_familia_id_tenant", x => new { x.id_familia, x.id_tenant });
                    table.ForeignKey(
                        name: "fk_familias_tenant",
                        column: x => x.id_tenant,
                        principalTable: "tenants",
                        principalColumn: "id_tenant",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateIndex(
                name: "ix_articulos_familia",
                table: "articulos",
                columns: new[] { "id_familia", "id_tenant" },
                filter: "id_familia IS NOT NULL");

            migrationBuilder.CreateIndex(
                name: "ux_familias_nombre",
                table: "familias",
                columns: new[] { "id_tenant", "nombre" },
                unique: true,
                filter: "deleted_at IS NULL");

            migrationBuilder.AddForeignKey(
                name: "fk_articulos_familia",
                table: "articulos",
                columns: new[] { "id_familia", "id_tenant" },
                principalTable: "familias",
                principalColumns: new[] { "id_familia", "id_tenant" },
                onDelete: ReferentialAction.Restrict);

            // RLS (ADR-4/ADR-15): la tabla nueva activa su policy en la misma migración que la
            // crea. Solo esquema, sin backfill: articulos.id_familia queda NULL en toda fila
            // existente, así que ningún UPDATE/INSERT sobre una tabla con RLS corre acá.
            migrationBuilder.HabilitarRlsDeTenant("familias");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropForeignKey(
                name: "fk_articulos_familia",
                table: "articulos");

            migrationBuilder.DropTable(
                name: "familias");

            migrationBuilder.DropIndex(
                name: "ix_articulos_familia",
                table: "articulos");

            migrationBuilder.DropColumn(
                name: "id_familia",
                table: "articulos");
        }
    }
}
