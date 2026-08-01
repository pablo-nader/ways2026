using Microsoft.EntityFrameworkCore.Migrations;
using Ways.Infrastructure.Multitenancy;

#nullable disable

namespace Ways.Infrastructure.Persistencia.Migraciones
{
    /// <inheritdoc />
    public partial class UsuariosMultiTenant : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "ux_usuarios_usuario",
                table: "usuarios");

            migrationBuilder.AddColumn<int>(
                name: "id_tenant",
                table: "usuarios",
                type: "integer",
                nullable: true);

            migrationBuilder.CreateIndex(
                name: "ix_usuarios_tenant",
                table: "usuarios",
                column: "id_tenant");

            migrationBuilder.CreateIndex(
                name: "ux_usuarios_usuario",
                table: "usuarios",
                columns: new[] { "id_tenant", "usuario" },
                unique: true,
                filter: "deleted_at IS NULL")
                .Annotation("Npgsql:NullsDistinct", false);

            migrationBuilder.AddForeignKey(
                name: "fk_usuarios_tenant",
                table: "usuarios",
                column: "id_tenant",
                principalTable: "tenants",
                principalColumn: "id_tenant",
                onDelete: ReferentialAction.Restrict);

            // --- Aislamiento por RLS sobre usuarios (doc 09, ADR-4, ADR-5, ADR-15) ---
            // Las funciones de contexto ya existen (creadas por la migración Organizacion);
            // usuarios es la primera tabla scopeada que además necesita la excepción de
            // login: antes de que exista una sesión, POST /api/auth/login tiene que poder
            // leer y actualizar CUALQUIER cuenta por mail, sin importar su tenant. El
            // scaffolder no sabe generar RLS, así que esto se agrega a mano igual que en la
            // migración Organizacion.
            migrationBuilder.HabilitarRlsDeTenant("usuarios");

            migrationBuilder.Sql(
                """
                CREATE POLICY usuarios_login_lectura ON usuarios
                    FOR SELECT USING (app_modo() = 'login');
                """);

            migrationBuilder.Sql(
                """
                CREATE POLICY usuarios_login_actualiza ON usuarios
                    FOR UPDATE USING (app_modo() = 'login')
                                WITH CHECK (app_modo() = 'login');
                """);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql("DROP POLICY IF EXISTS usuarios_login_actualiza ON usuarios;");
            migrationBuilder.Sql("DROP POLICY IF EXISTS usuarios_login_lectura ON usuarios;");
            migrationBuilder.Sql("DROP POLICY IF EXISTS usuarios_tenant ON usuarios;");
            migrationBuilder.Sql("ALTER TABLE usuarios NO FORCE ROW LEVEL SECURITY;");
            migrationBuilder.Sql("ALTER TABLE usuarios DISABLE ROW LEVEL SECURITY;");

            migrationBuilder.DropForeignKey(
                name: "fk_usuarios_tenant",
                table: "usuarios");

            migrationBuilder.DropIndex(
                name: "ix_usuarios_tenant",
                table: "usuarios");

            migrationBuilder.DropIndex(
                name: "ux_usuarios_usuario",
                table: "usuarios");

            migrationBuilder.DropColumn(
                name: "id_tenant",
                table: "usuarios");

            migrationBuilder.CreateIndex(
                name: "ux_usuarios_usuario",
                table: "usuarios",
                column: "usuario",
                unique: true,
                filter: "deleted_at IS NULL");
        }
    }
}
