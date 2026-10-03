using System;
using Microsoft.EntityFrameworkCore.Migrations;
using Npgsql.EntityFrameworkCore.PostgreSQL.Metadata;
using Ways.Infrastructure.Multitenancy;

#nullable disable

namespace Ways.Infrastructure.Persistencia.Migraciones
{
    /// <inheritdoc />
    public partial class CodigosProveedor : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "codigos_proveedor",
                columns: table => new
                {
                    id_codigo_proveedor = table.Column<int>(type: "integer", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    id_articulo = table.Column<int>(type: "integer", nullable: false),
                    id_proveedor = table.Column<int>(type: "integer", nullable: false),
                    codigo = table.Column<string>(type: "citext", maxLength: 50, nullable: false),
                    created_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    updated_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    deleted_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    id_tenant = table.Column<int>(type: "integer", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_codigos_proveedor", x => x.id_codigo_proveedor);
                    table.CheckConstraint("ck_codigos_proveedor_codigo_normalizado", "codigo = btrim(codigo) AND codigo <> ''");
                    table.ForeignKey(
                        name: "fk_codigos_proveedor_articulo",
                        columns: x => new { x.id_articulo, x.id_tenant },
                        principalTable: "articulos",
                        principalColumns: new[] { "id_articulo", "id_tenant" },
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "fk_codigos_proveedor_proveedor",
                        columns: x => new { x.id_proveedor, x.id_tenant },
                        principalTable: "proveedores",
                        principalColumns: new[] { "id_proveedor", "id_tenant" },
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "fk_codigos_proveedor_tenant",
                        column: x => x.id_tenant,
                        principalTable: "tenants",
                        principalColumn: "id_tenant",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateIndex(
                name: "ix_codigos_proveedor_articulo",
                table: "codigos_proveedor",
                columns: new[] { "id_articulo", "id_tenant" });

            migrationBuilder.CreateIndex(
                name: "ix_codigos_proveedor_proveedor",
                table: "codigos_proveedor",
                columns: new[] { "id_proveedor", "id_tenant" });

            migrationBuilder.CreateIndex(
                name: "ix_codigos_proveedor_tenant",
                table: "codigos_proveedor",
                column: "id_tenant");

            migrationBuilder.CreateIndex(
                name: "ux_codigos_proveedor_proveedor_codigo",
                table: "codigos_proveedor",
                columns: new[] { "id_tenant", "id_proveedor", "codigo" },
                unique: true,
                filter: "deleted_at IS NULL");

            // Tabla de tenant como codigos_barra: RLS por id_tenant y FORCE. Sin backfill.
            migrationBuilder.HabilitarRlsDeTenant("codigos_proveedor");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "codigos_proveedor");
        }
    }
}
