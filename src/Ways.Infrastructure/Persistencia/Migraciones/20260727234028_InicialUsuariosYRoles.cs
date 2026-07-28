using System;
using Microsoft.EntityFrameworkCore.Migrations;
using Npgsql.EntityFrameworkCore.PostgreSQL.Metadata;
using Ways.Domain.Usuarios;

#nullable disable

namespace Ways.Infrastructure.Persistencia.Migraciones
{
    /// <inheritdoc />
    public partial class InicialUsuariosYRoles : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AlterDatabase()
                .Annotation("Npgsql:Enum:estado_usuario", "activo,bloqueado,inactivo")
                .Annotation("Npgsql:PostgresExtension:citext", ",,");

            migrationBuilder.CreateTable(
                name: "roles",
                columns: table => new
                {
                    id_rol = table.Column<int>(type: "integer", nullable: false),
                    nombre = table.Column<string>(type: "citext", maxLength: 50, nullable: false),
                    descripcion = table.Column<string>(type: "character varying(255)", maxLength: 255, nullable: true),
                    created_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    updated_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    deleted_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_roles", x => x.id_rol);
                });

            migrationBuilder.CreateTable(
                name: "usuarios",
                columns: table => new
                {
                    id_usuario = table.Column<int>(type: "integer", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    usuario = table.Column<string>(type: "citext", maxLength: 40, nullable: false),
                    mail = table.Column<string>(type: "citext", maxLength: 255, nullable: false),
                    id_rol = table.Column<int>(type: "integer", nullable: false),
                    estado = table.Column<EstadoUsuario>(type: "estado_usuario", nullable: false, defaultValue: EstadoUsuario.Activo),
                    password_hash = table.Column<string>(type: "character varying(255)", maxLength: 255, nullable: false),
                    password_algoritmo = table.Column<string>(type: "character varying(30)", maxLength: 30, nullable: false),
                    password_actualizado_el = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    ultima_conexion = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    ultimo_intento_fallido = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    intentos_fallidos = table.Column<short>(type: "smallint", nullable: false, defaultValue: (short)0),
                    created_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    updated_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    deleted_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_usuarios", x => x.id_usuario);
                    table.ForeignKey(
                        name: "fk_usuarios_rol",
                        column: x => x.id_rol,
                        principalTable: "roles",
                        principalColumn: "id_rol",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateIndex(
                name: "ux_roles_nombre",
                table: "roles",
                column: "nombre",
                unique: true,
                filter: "deleted_at IS NULL");

            migrationBuilder.CreateIndex(
                name: "ix_usuarios_rol",
                table: "usuarios",
                column: "id_rol");

            migrationBuilder.CreateIndex(
                name: "ux_usuarios_mail",
                table: "usuarios",
                column: "mail",
                unique: true,
                filter: "deleted_at IS NULL");

            migrationBuilder.CreateIndex(
                name: "ux_usuarios_usuario",
                table: "usuarios",
                column: "usuario",
                unique: true,
                filter: "deleted_at IS NULL");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "usuarios");

            migrationBuilder.DropTable(
                name: "roles");
        }
    }
}
