using System;
using Microsoft.EntityFrameworkCore.Migrations;
using Npgsql.EntityFrameworkCore.PostgreSQL.Metadata;
using Ways.Infrastructure.Multitenancy;

#nullable disable

namespace Ways.Infrastructure.Persistencia.Migraciones
{
    /// <inheritdoc />
    public partial class DominiosPorTenant : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "dominios",
                columns: table => new
                {
                    id_dominio = table.Column<int>(type: "integer", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    host = table.Column<string>(type: "text", nullable: false),
                    es_principal = table.Column<bool>(type: "boolean", nullable: false, defaultValue: false),
                    verificado_en = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    created_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    updated_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    deleted_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    id_tenant = table.Column<int>(type: "integer", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_dominios", x => x.id_dominio);
                    table.CheckConstraint("ck_dominios_host_formato", "host = lower(host) AND char_length(host) <= 253 AND host ~ '^[a-z0-9]([a-z0-9-]{0,61}[a-z0-9])?(\\.[a-z0-9]([a-z0-9-]{0,61}[a-z0-9])?)*$'");
                    table.ForeignKey(
                        name: "fk_dominios_tenant",
                        column: x => x.id_tenant,
                        principalTable: "tenants",
                        principalColumn: "id_tenant",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateIndex(
                name: "ix_dominios_tenant",
                table: "dominios",
                column: "id_tenant");

            migrationBuilder.CreateIndex(
                name: "ux_dominios_host",
                table: "dominios",
                column: "host",
                unique: true,
                filter: "deleted_at IS NULL");

            migrationBuilder.CreateIndex(
                name: "ux_dominios_principal_por_tenant",
                table: "dominios",
                column: "id_tenant",
                unique: true,
                filter: "es_principal AND deleted_at IS NULL");

            // --- Aislamiento por RLS sobre dominios (doc 09, ADR-4, ADR-15) ---
            // Solo DDL: la migración no escribe datos, así que no aplica el SET LOCAL de los
            // backfills. dominios_login_lectura copia usuarios_login_lectura: el host se resuelve
            // antes de que exista una sesión, y esa lectura corre en modo login.
            migrationBuilder.HabilitarRlsDeTenant("dominios");

            migrationBuilder.Sql(
                """
                CREATE POLICY dominios_login_lectura ON dominios
                    FOR SELECT USING (app_modo() = 'login');
                """);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql("DROP POLICY IF EXISTS dominios_login_lectura ON dominios;");
            migrationBuilder.Sql("DROP POLICY IF EXISTS dominios_tenant ON dominios;");

            migrationBuilder.DropTable(
                name: "dominios");
        }
    }
}
