using System;
using Microsoft.EntityFrameworkCore.Migrations;
using Npgsql.EntityFrameworkCore.PostgreSQL.Metadata;
using Ways.Infrastructure.Multitenancy;

#nullable disable

namespace Ways.Infrastructure.Persistencia.Migraciones
{
    /// <inheritdoc />
    public partial class CatalogoOnline : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<int>(
                name: "id_lista_precio",
                table: "vitrinas",
                type: "integer",
                nullable: true);

            migrationBuilder.AddColumn<bool>(
                name: "publicado_web",
                table: "articulos",
                type: "boolean",
                nullable: false,
                defaultValue: false);

            migrationBuilder.CreateTable(
                name: "imagenes_articulo",
                columns: table => new
                {
                    id_imagen_articulo = table.Column<int>(type: "integer", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    id_articulo = table.Column<int>(type: "integer", nullable: false),
                    contenido = table.Column<byte[]>(type: "bytea", nullable: false),
                    content_type = table.Column<string>(type: "text", nullable: false),
                    actualizado_en = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    created_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    updated_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    deleted_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    id_tenant = table.Column<int>(type: "integer", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_imagenes_articulo", x => x.id_imagen_articulo);
                    table.CheckConstraint("ck_imagenes_articulo_contenido", "octet_length(contenido) BETWEEN 1 AND 1048576 AND content_type IS NOT NULL AND content_type IN ('image/png', 'image/jpeg', 'image/webp')");
                    table.ForeignKey(
                        name: "fk_imagenes_articulo_articulo",
                        columns: x => new { x.id_articulo, x.id_tenant },
                        principalTable: "articulos",
                        principalColumns: new[] { "id_articulo", "id_tenant" },
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "fk_imagenes_articulo_tenant",
                        column: x => x.id_tenant,
                        principalTable: "tenants",
                        principalColumn: "id_tenant",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateIndex(
                name: "ix_vitrinas_lista_precio",
                table: "vitrinas",
                columns: new[] { "id_lista_precio", "id_tenant" });

            migrationBuilder.CreateIndex(
                name: "ix_articulos_publicado_web",
                table: "articulos",
                column: "id_tenant",
                filter: "publicado_web AND activo AND deleted_at IS NULL");

            migrationBuilder.CreateIndex(
                name: "ix_imagenes_articulo_articulo",
                table: "imagenes_articulo",
                columns: new[] { "id_articulo", "id_tenant" });

            migrationBuilder.CreateIndex(
                name: "ix_imagenes_articulo_tenant",
                table: "imagenes_articulo",
                column: "id_tenant");

            migrationBuilder.CreateIndex(
                name: "ux_imagenes_articulo_articulo",
                table: "imagenes_articulo",
                column: "id_articulo",
                unique: true,
                filter: "deleted_at IS NULL");

            migrationBuilder.AddForeignKey(
                name: "fk_vitrinas_lista_precio",
                table: "vitrinas",
                columns: new[] { "id_lista_precio", "id_tenant" },
                principalTable: "listas_precio",
                principalColumns: new[] { "id_lista_precio", "id_tenant" },
                onDelete: ReferentialAction.Restrict);

            // --- Aislamiento por RLS sobre imagenes_articulo (doc 09, ADR-4, ADR-15) y lectura publica ---
            // Solo DDL: la migración no escribe datos (publicado_web nace en false para todo artículo), así que
            // no aplica el SET LOCAL de los backfills.
            migrationBuilder.HabilitarRlsDeTenant("imagenes_articulo");

            // Seis tablas más para el modo publico, que ya existe (migración Vitrinas): SELECT únicamente, solo
            // las filas del tenant del GUC público y solo las vivas. articulos además exige publicado y activo,
            // que es lo que decide qué artículos puede ver la tienda; el resto del catálogo (disponibilidad,
            // precio vigente, categoría visible) lo decide la consulta. Las demás tablas del comercio siguen sin
            // policy de este modo.
            migrationBuilder.HabilitarLecturaPublica("articulos", "publicado_web AND activo AND deleted_at IS NULL");
            migrationBuilder.HabilitarLecturaPublica("articulos_empresas");
            migrationBuilder.HabilitarLecturaPublica("listas_precio", "deleted_at IS NULL");
            migrationBuilder.HabilitarLecturaPublica("precios", "deleted_at IS NULL");
            migrationBuilder.HabilitarLecturaPublica("categorias", "deleted_at IS NULL");
            migrationBuilder.HabilitarLecturaPublica("imagenes_articulo", "deleted_at IS NULL");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.QuitarLecturaPublica("imagenes_articulo");
            migrationBuilder.QuitarLecturaPublica("categorias");
            migrationBuilder.QuitarLecturaPublica("precios");
            migrationBuilder.QuitarLecturaPublica("listas_precio");
            migrationBuilder.QuitarLecturaPublica("articulos_empresas");
            migrationBuilder.QuitarLecturaPublica("articulos");
            migrationBuilder.Sql("DROP POLICY IF EXISTS imagenes_articulo_tenant ON imagenes_articulo;");

            migrationBuilder.DropForeignKey(
                name: "fk_vitrinas_lista_precio",
                table: "vitrinas");

            migrationBuilder.DropTable(
                name: "imagenes_articulo");

            migrationBuilder.DropIndex(
                name: "ix_vitrinas_lista_precio",
                table: "vitrinas");

            migrationBuilder.DropIndex(
                name: "ix_articulos_publicado_web",
                table: "articulos");

            migrationBuilder.DropColumn(
                name: "id_lista_precio",
                table: "vitrinas");

            migrationBuilder.DropColumn(
                name: "publicado_web",
                table: "articulos");
        }
    }
}
