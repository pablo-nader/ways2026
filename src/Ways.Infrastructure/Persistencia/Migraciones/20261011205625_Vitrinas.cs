using System;
using Microsoft.EntityFrameworkCore.Migrations;
using Npgsql.EntityFrameworkCore.PostgreSQL.Metadata;
using Ways.Infrastructure.Multitenancy;

#nullable disable

namespace Ways.Infrastructure.Persistencia.Migraciones
{
    /// <inheritdoc />
    public partial class Vitrinas : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "vitrinas",
                columns: table => new
                {
                    id_vitrina = table.Column<int>(type: "integer", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    id_empresa = table.Column<int>(type: "integer", nullable: false),
                    publicada = table.Column<bool>(type: "boolean", nullable: false, defaultValue: false),
                    tagline = table.Column<string>(type: "text", nullable: true),
                    descripcion = table.Column<string>(type: "text", nullable: true),
                    color_acento = table.Column<string>(type: "text", nullable: true),
                    tema = table.Column<string>(type: "text", nullable: false, defaultValue: "oscuro"),
                    logo = table.Column<byte[]>(type: "bytea", nullable: true),
                    logo_content_type = table.Column<string>(type: "text", nullable: true),
                    logo_actualizado_en = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    created_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    updated_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    deleted_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    id_tenant = table.Column<int>(type: "integer", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_vitrinas", x => x.id_vitrina);
                    table.CheckConstraint("ck_vitrinas_color_acento", "color_acento IS NULL OR color_acento ~ '^#[0-9a-f]{6}$'");
                    table.CheckConstraint("ck_vitrinas_descripcion_largo", "descripcion IS NULL OR char_length(descripcion) <= 2000");
                    table.CheckConstraint("ck_vitrinas_logo", "(logo IS NULL AND logo_content_type IS NULL AND logo_actualizado_en IS NULL) OR (logo IS NOT NULL AND octet_length(logo) BETWEEN 1 AND 1048576 AND logo_content_type IS NOT NULL AND logo_content_type IN ('image/png', 'image/jpeg', 'image/webp') AND logo_actualizado_en IS NOT NULL)");
                    table.CheckConstraint("ck_vitrinas_tagline_largo", "tagline IS NULL OR char_length(tagline) <= 120");
                    table.CheckConstraint("ck_vitrinas_tema", "tema IN ('claro', 'oscuro')");
                    table.ForeignKey(
                        name: "fk_vitrinas_empresa",
                        columns: x => new { x.id_empresa, x.id_tenant },
                        principalTable: "empresas",
                        principalColumns: new[] { "id_empresa", "id_tenant" },
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "fk_vitrinas_tenant",
                        column: x => x.id_tenant,
                        principalTable: "tenants",
                        principalColumn: "id_tenant",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateIndex(
                name: "ix_vitrinas_empresa",
                table: "vitrinas",
                columns: new[] { "id_empresa", "id_tenant" });

            migrationBuilder.CreateIndex(
                name: "ix_vitrinas_tenant",
                table: "vitrinas",
                column: "id_tenant");

            migrationBuilder.CreateIndex(
                name: "ux_vitrinas_empresa",
                table: "vitrinas",
                column: "id_empresa",
                unique: true,
                filter: "deleted_at IS NULL");

            migrationBuilder.CreateIndex(
                name: "ux_vitrinas_publicada_por_tenant",
                table: "vitrinas",
                column: "id_tenant",
                unique: true,
                filter: "publicada AND deleted_at IS NULL");

            // --- Aislamiento por RLS sobre vitrinas (doc 09, ADR-4, ADR-15) y modo de acceso publico ---
            // Solo DDL: la migración no escribe datos, así que no aplica el SET LOCAL de los backfills.
            migrationBuilder.HabilitarRlsDeTenant("vitrinas");

            // El modo publico lleva su tenant en app.tenant_publico y NO en app.tenant_id: las policies
            // {tabla}_tenant no miran el modo, y con el tenant en app.tenant_id este modo vería todas las
            // tablas del comercio. Lectura únicamente (FOR SELECT), sobre exactamente estas tres tablas.
            migrationBuilder.CrearFuncionDeTenantPublico();
            migrationBuilder.HabilitarLecturaPublica("vitrinas", "publicada AND deleted_at IS NULL");
            migrationBuilder.HabilitarLecturaPublica("empresas");
            migrationBuilder.HabilitarLecturaPublica("puntos_venta");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.QuitarLecturaPublica("puntos_venta");
            migrationBuilder.QuitarLecturaPublica("empresas");
            migrationBuilder.QuitarLecturaPublica("vitrinas");
            migrationBuilder.Sql("DROP POLICY IF EXISTS vitrinas_tenant ON vitrinas;");

            // Después de las tres policies que la usan.
            migrationBuilder.QuitarFuncionDeTenantPublico();

            migrationBuilder.DropTable(
                name: "vitrinas");
        }
    }
}
