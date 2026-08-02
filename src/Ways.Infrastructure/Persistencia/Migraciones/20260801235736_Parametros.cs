using System;
using Microsoft.EntityFrameworkCore.Migrations;
using Npgsql.EntityFrameworkCore.PostgreSQL.Metadata;
using Ways.Infrastructure.Multitenancy;

#nullable disable

namespace Ways.Infrastructure.Persistencia.Migraciones
{
    /// <inheritdoc />
    public partial class Parametros : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "parametros",
                columns: table => new
                {
                    id_parametro = table.Column<int>(type: "integer", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    id_empresa = table.Column<int>(type: "integer", nullable: false),
                    id_punto_venta = table.Column<int>(type: "integer", nullable: true),
                    clave = table.Column<string>(type: "citext", maxLength: 80, nullable: false),
                    valor = table.Column<string>(type: "jsonb", nullable: false),
                    created_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    updated_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    deleted_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    id_tenant = table.Column<int>(type: "integer", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_parametros", x => x.id_parametro);
                    table.ForeignKey(
                        name: "fk_parametros_empresa",
                        columns: x => new { x.id_empresa, x.id_tenant },
                        principalTable: "empresas",
                        principalColumns: new[] { "id_empresa", "id_tenant" },
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "fk_parametros_punto_venta",
                        columns: x => new { x.id_punto_venta, x.id_tenant },
                        principalTable: "puntos_venta",
                        principalColumns: new[] { "id_punto_venta", "id_tenant" },
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "fk_parametros_tenant",
                        column: x => x.id_tenant,
                        principalTable: "tenants",
                        principalColumn: "id_tenant",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateIndex(
                name: "ix_parametros_empresa",
                table: "parametros",
                columns: new[] { "id_empresa", "id_tenant" });

            migrationBuilder.CreateIndex(
                name: "ix_parametros_punto_venta",
                table: "parametros",
                columns: new[] { "id_punto_venta", "id_tenant" });

            migrationBuilder.CreateIndex(
                name: "ix_parametros_tenant",
                table: "parametros",
                column: "id_tenant");

            migrationBuilder.CreateIndex(
                name: "ux_parametros_empresa",
                table: "parametros",
                columns: new[] { "id_tenant", "id_empresa", "clave" },
                unique: true,
                filter: "id_punto_venta IS NULL AND deleted_at IS NULL");

            migrationBuilder.CreateIndex(
                name: "ux_parametros_punto_venta",
                table: "parametros",
                columns: new[] { "id_tenant", "id_empresa", "id_punto_venta", "clave" },
                unique: true,
                filter: "id_punto_venta IS NOT NULL AND deleted_at IS NULL");

            // RLS estándar (ADR-4/ADR-15): parametros es una tabla de tenant normal, no un
            // catálogo global — el patrón de gate #4 no aplica acá.
            migrationBuilder.HabilitarRlsDeTenant("parametros");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "parametros");
        }
    }
}
