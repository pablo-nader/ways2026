using System;
using Microsoft.EntityFrameworkCore.Migrations;
using Npgsql.EntityFrameworkCore.PostgreSQL.Metadata;
using Ways.Infrastructure.Multitenancy;

#nullable disable

namespace Ways.Infrastructure.Persistencia.Migraciones
{
    /// <inheritdoc />
    public partial class DispositivosPos : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "dispositivos",
                columns: table => new
                {
                    id_dispositivo = table.Column<int>(type: "integer", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    id_punto_venta = table.Column<int>(type: "integer", nullable: false),
                    nombre = table.Column<string>(type: "character varying(100)", maxLength: 100, nullable: false),
                    token_hash = table.Column<string>(type: "char(64)", fixedLength: true, nullable: false),
                    id_usuario_alta = table.Column<int>(type: "integer", nullable: false),
                    ultimo_uso_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    created_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    updated_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    deleted_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    id_tenant = table.Column<int>(type: "integer", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_dispositivos", x => x.id_dispositivo);
                    table.ForeignKey(
                        name: "fk_dispositivos_punto_venta",
                        columns: x => new { x.id_punto_venta, x.id_tenant },
                        principalTable: "puntos_venta",
                        principalColumns: new[] { "id_punto_venta", "id_tenant" },
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "fk_dispositivos_tenant",
                        column: x => x.id_tenant,
                        principalTable: "tenants",
                        principalColumn: "id_tenant",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "fk_dispositivos_usuario_alta",
                        column: x => x.id_usuario_alta,
                        principalTable: "usuarios",
                        principalColumn: "id_usuario",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateIndex(
                name: "ix_dispositivos_punto_venta",
                table: "dispositivos",
                columns: new[] { "id_punto_venta", "id_tenant" });

            migrationBuilder.CreateIndex(
                name: "ix_dispositivos_tenant_punto_venta",
                table: "dispositivos",
                columns: new[] { "id_tenant", "id_punto_venta" });

            migrationBuilder.CreateIndex(
                name: "ix_dispositivos_usuario_alta",
                table: "dispositivos",
                column: "id_usuario_alta");

            migrationBuilder.CreateIndex(
                name: "ux_dispositivos_token_hash",
                table: "dispositivos",
                column: "token_hash",
                unique: true);

            // --- Aislamiento por RLS sobre dispositivos (doc 09, ADR-4/ADR-5/ADR-15) ---
            // Las funciones de contexto ya existen (creadas por la migración Organizacion).
            // dispositivos necesita la misma excepción de login que usuarios (migración
            // UsuariosMultiTenant): el dispositivo se tiene que poder resolver por token ANTES
            // de que exista una sesión, sin importar de qué tenant es — de ahí la policy de
            // SOLO LECTURA en modo login (a diferencia de usuarios, que además necesita
            // usuarios_login_actualiza: acá no hace falta, ServicioDeDispositivos pasa el
            // contexto a modo Tenant en cuanto conoce el tenant del dispositivo, y recién ahí
            // escribe ultimo_uso_at bajo la policy estándar dispositivos_tenant).
            migrationBuilder.HabilitarRlsDeTenant("dispositivos");

            migrationBuilder.Sql(
                """
                CREATE POLICY dispositivos_login_lectura ON dispositivos
                    FOR SELECT USING (app_modo() = 'login');
                """);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql("DROP POLICY IF EXISTS dispositivos_login_lectura ON dispositivos;");
            migrationBuilder.Sql("DROP POLICY IF EXISTS dispositivos_tenant ON dispositivos;");
            migrationBuilder.Sql("ALTER TABLE dispositivos NO FORCE ROW LEVEL SECURITY;");
            migrationBuilder.Sql("ALTER TABLE dispositivos DISABLE ROW LEVEL SECURITY;");

            migrationBuilder.DropTable(
                name: "dispositivos");
        }
    }
}
