using System;
using Microsoft.EntityFrameworkCore.Migrations;
using Npgsql.EntityFrameworkCore.PostgreSQL.Metadata;
using Ways.Infrastructure.Multitenancy;

#nullable disable

namespace Ways.Infrastructure.Persistencia.Migraciones
{
    /// <inheritdoc />
    public partial class Combos : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "combos",
                columns: table => new
                {
                    id_combo = table.Column<int>(type: "integer", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    id_empresa = table.Column<int>(type: "integer", nullable: true),
                    codigo = table.Column<int>(type: "integer", nullable: false),
                    nombre = table.Column<string>(type: "citext", maxLength: 150, nullable: false),
                    precio = table.Column<decimal>(type: "numeric(14,2)", nullable: false),
                    activo = table.Column<bool>(type: "boolean", nullable: false, defaultValue: true),
                    created_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    updated_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    deleted_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    id_tenant = table.Column<int>(type: "integer", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_combos", x => x.id_combo);
                    table.UniqueConstraint("ak_combos_id_combo_id_tenant", x => new { x.id_combo, x.id_tenant });
                    table.CheckConstraint("ck_combos_codigo_positivo", "codigo > 0");
                    table.CheckConstraint("ck_combos_precio_positivo", "precio > 0");
                    table.ForeignKey(
                        name: "fk_combos_empresa",
                        columns: x => new { x.id_empresa, x.id_tenant },
                        principalTable: "empresas",
                        principalColumns: new[] { "id_empresa", "id_tenant" },
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "fk_combos_tenant",
                        column: x => x.id_tenant,
                        principalTable: "tenants",
                        principalColumn: "id_tenant",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "combos_componentes",
                columns: table => new
                {
                    id_combo_componente = table.Column<int>(type: "integer", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    id_tenant = table.Column<int>(type: "integer", nullable: false),
                    id_combo = table.Column<int>(type: "integer", nullable: false),
                    id_articulo = table.Column<int>(type: "integer", nullable: true),
                    id_familia = table.Column<int>(type: "integer", nullable: true),
                    cantidad = table.Column<decimal>(type: "numeric(12,3)", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_combos_componentes", x => x.id_combo_componente);
                    table.CheckConstraint("ck_combos_componentes_alcance_exclusivo", "num_nonnulls(id_articulo, id_familia) = 1");
                    table.CheckConstraint("ck_combos_componentes_cantidad_positiva", "cantidad > 0");
                    table.ForeignKey(
                        name: "fk_combos_componentes_articulo",
                        columns: x => new { x.id_articulo, x.id_tenant },
                        principalTable: "articulos",
                        principalColumns: new[] { "id_articulo", "id_tenant" },
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "fk_combos_componentes_combo",
                        columns: x => new { x.id_combo, x.id_tenant },
                        principalTable: "combos",
                        principalColumns: new[] { "id_combo", "id_tenant" },
                        onDelete: ReferentialAction.Cascade);
                    table.ForeignKey(
                        name: "fk_combos_componentes_familia",
                        columns: x => new { x.id_familia, x.id_tenant },
                        principalTable: "familias",
                        principalColumns: new[] { "id_familia", "id_tenant" },
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "fk_combos_componentes_tenant",
                        column: x => x.id_tenant,
                        principalTable: "tenants",
                        principalColumn: "id_tenant",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateIndex(
                name: "ix_combos_empresa",
                table: "combos",
                columns: new[] { "id_empresa", "id_tenant" });

            migrationBuilder.CreateIndex(
                name: "ix_combos_tenant",
                table: "combos",
                column: "id_tenant");

            migrationBuilder.CreateIndex(
                name: "ux_combos_codigo",
                table: "combos",
                columns: new[] { "id_tenant", "codigo" },
                unique: true,
                filter: "deleted_at IS NULL");

            migrationBuilder.CreateIndex(
                name: "ix_combos_componentes_articulo",
                table: "combos_componentes",
                columns: new[] { "id_articulo", "id_tenant" });

            migrationBuilder.CreateIndex(
                name: "ix_combos_componentes_combo",
                table: "combos_componentes",
                columns: new[] { "id_combo", "id_tenant" });

            migrationBuilder.CreateIndex(
                name: "ix_combos_componentes_familia",
                table: "combos_componentes",
                columns: new[] { "id_familia", "id_tenant" });

            migrationBuilder.CreateIndex(
                name: "ix_combos_componentes_tenant",
                table: "combos_componentes",
                column: "id_tenant");

            migrationBuilder.CreateIndex(
                name: "ux_combos_componentes_articulo",
                table: "combos_componentes",
                columns: new[] { "id_combo", "id_articulo" },
                unique: true,
                filter: "id_articulo IS NOT NULL");

            migrationBuilder.CreateIndex(
                name: "ux_combos_componentes_familia",
                table: "combos_componentes",
                columns: new[] { "id_combo", "id_familia" },
                unique: true,
                filter: "id_familia IS NOT NULL");

            // RLS: las 2 tablas nuevas activan su policy en la misma migración que las crea.
            migrationBuilder.HabilitarRlsDeTenant("combos");
            migrationBuilder.HabilitarRlsDeTenant("combos_componentes");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "combos_componentes");

            migrationBuilder.DropTable(
                name: "combos");
        }
    }
}
