using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Ways.Api.ConectorMcp.Migraciones
{
    /// <inheritdoc />
    public partial class AlmacenOAuthDelConector : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "alcances_oauth",
                columns: table => new
                {
                    id_alcance_oauth = table.Column<string>(type: "text", nullable: false),
                    token_concurrencia = table.Column<string>(type: "character varying(50)", maxLength: 50, nullable: true),
                    descripcion = table.Column<string>(type: "text", nullable: true),
                    descripciones = table.Column<string>(type: "text", nullable: true),
                    nombre_visible = table.Column<string>(type: "text", nullable: true),
                    nombres_visibles = table.Column<string>(type: "text", nullable: true),
                    nombre = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: true),
                    propiedades = table.Column<string>(type: "text", nullable: true),
                    recursos = table.Column<string>(type: "text", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_alcances_oauth", x => x.id_alcance_oauth);
                });

            migrationBuilder.CreateTable(
                name: "aplicaciones_oauth",
                columns: table => new
                {
                    id_aplicacion_oauth = table.Column<string>(type: "text", nullable: false),
                    tipo_aplicacion = table.Column<string>(type: "character varying(50)", maxLength: 50, nullable: true),
                    id_cliente = table.Column<string>(type: "character varying(100)", maxLength: 100, nullable: true),
                    secreto_cliente = table.Column<string>(type: "text", nullable: true),
                    tipo_cliente = table.Column<string>(type: "character varying(50)", maxLength: 50, nullable: true),
                    token_concurrencia = table.Column<string>(type: "character varying(50)", maxLength: 50, nullable: true),
                    tipo_consentimiento = table.Column<string>(type: "character varying(50)", maxLength: 50, nullable: true),
                    nombre_visible = table.Column<string>(type: "text", nullable: true),
                    nombres_visibles = table.Column<string>(type: "text", nullable: true),
                    claves_json_web = table.Column<string>(type: "text", nullable: true),
                    permisos = table.Column<string>(type: "text", nullable: true),
                    uris_redireccion_cierre_sesion = table.Column<string>(type: "text", nullable: true),
                    propiedades = table.Column<string>(type: "text", nullable: true),
                    uris_redireccion = table.Column<string>(type: "text", nullable: true),
                    requisitos = table.Column<string>(type: "text", nullable: true),
                    ajustes = table.Column<string>(type: "text", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_aplicaciones_oauth", x => x.id_aplicacion_oauth);
                });

            migrationBuilder.CreateTable(
                name: "autorizaciones_oauth",
                columns: table => new
                {
                    id_autorizacion_oauth = table.Column<string>(type: "text", nullable: false),
                    id_aplicacion_oauth = table.Column<string>(type: "text", nullable: true),
                    token_concurrencia = table.Column<string>(type: "character varying(50)", maxLength: 50, nullable: true),
                    fecha_creacion = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    propiedades = table.Column<string>(type: "text", nullable: true),
                    alcances = table.Column<string>(type: "text", nullable: true),
                    estado = table.Column<string>(type: "character varying(50)", maxLength: 50, nullable: true),
                    sujeto = table.Column<string>(type: "character varying(400)", maxLength: 400, nullable: true),
                    tipo = table.Column<string>(type: "character varying(50)", maxLength: 50, nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_autorizaciones_oauth", x => x.id_autorizacion_oauth);
                    table.ForeignKey(
                        name: "fk_autorizaciones_oauth_aplicacion",
                        column: x => x.id_aplicacion_oauth,
                        principalTable: "aplicaciones_oauth",
                        principalColumn: "id_aplicacion_oauth");
                });

            migrationBuilder.CreateTable(
                name: "tokens_oauth",
                columns: table => new
                {
                    id_token_oauth = table.Column<string>(type: "text", nullable: false),
                    id_aplicacion_oauth = table.Column<string>(type: "text", nullable: true),
                    id_autorizacion_oauth = table.Column<string>(type: "text", nullable: true),
                    token_concurrencia = table.Column<string>(type: "character varying(50)", maxLength: 50, nullable: true),
                    fecha_creacion = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    fecha_vencimiento = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    contenido = table.Column<string>(type: "text", nullable: true),
                    propiedades = table.Column<string>(type: "text", nullable: true),
                    fecha_canje = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    id_referencia = table.Column<string>(type: "character varying(100)", maxLength: 100, nullable: true),
                    estado = table.Column<string>(type: "character varying(50)", maxLength: 50, nullable: true),
                    sujeto = table.Column<string>(type: "character varying(400)", maxLength: 400, nullable: true),
                    tipo = table.Column<string>(type: "character varying(150)", maxLength: 150, nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_tokens_oauth", x => x.id_token_oauth);
                    table.ForeignKey(
                        name: "fk_tokens_oauth_aplicacion",
                        column: x => x.id_aplicacion_oauth,
                        principalTable: "aplicaciones_oauth",
                        principalColumn: "id_aplicacion_oauth");
                    table.ForeignKey(
                        name: "fk_tokens_oauth_autorizacion",
                        column: x => x.id_autorizacion_oauth,
                        principalTable: "autorizaciones_oauth",
                        principalColumn: "id_autorizacion_oauth");
                });

            migrationBuilder.CreateIndex(
                name: "ux_alcances_oauth_nombre",
                table: "alcances_oauth",
                column: "nombre",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "ux_aplicaciones_oauth_id_cliente",
                table: "aplicaciones_oauth",
                column: "id_cliente",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "ix_autorizaciones_oauth_aplicacion_estado_sujeto_tipo",
                table: "autorizaciones_oauth",
                columns: new[] { "id_aplicacion_oauth", "estado", "sujeto", "tipo" });

            migrationBuilder.CreateIndex(
                name: "ix_tokens_oauth_aplicacion_estado_sujeto_tipo",
                table: "tokens_oauth",
                columns: new[] { "id_aplicacion_oauth", "estado", "sujeto", "tipo" });

            migrationBuilder.CreateIndex(
                name: "ix_tokens_oauth_autorizacion",
                table: "tokens_oauth",
                column: "id_autorizacion_oauth");

            migrationBuilder.CreateIndex(
                name: "ux_tokens_oauth_id_referencia",
                table: "tokens_oauth",
                column: "id_referencia",
                unique: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "alcances_oauth");

            migrationBuilder.DropTable(
                name: "tokens_oauth");

            migrationBuilder.DropTable(
                name: "autorizaciones_oauth");

            migrationBuilder.DropTable(
                name: "aplicaciones_oauth");
        }
    }
}
