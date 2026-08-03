using System;
using Microsoft.EntityFrameworkCore.Migrations;
using Npgsql.EntityFrameworkCore.PostgreSQL.Metadata;
using Ways.Domain.Articulos;
using Ways.Infrastructure.Multitenancy;

#nullable disable

namespace Ways.Infrastructure.Persistencia.Migraciones
{
    /// <inheritdoc />
    public partial class ArticulosYPreciosEtapa3 : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AlterDatabase()
                .Annotation("Npgsql:Enum:clase_comprobante", "compra,venta")
                .Annotation("Npgsql:Enum:comportamiento_medio_pago", "cuenta_corriente,efectivo,electronico")
                .Annotation("Npgsql:Enum:estado_tenant", "activo,baja,suspendido")
                .Annotation("Npgsql:Enum:estado_usuario", "activo,bloqueado,inactivo")
                .Annotation("Npgsql:Enum:modo_lista", "derivada,fija")
                .Annotation("Npgsql:Enum:tipo_documento", "cuil,cuit,dni,otro,pasaporte")
                .Annotation("Npgsql:Enum:unidad_venta", "peso,unidad")
                .Annotation("Npgsql:PostgresExtension:citext", ",,")
                .OldAnnotation("Npgsql:Enum:clase_comprobante", "compra,venta")
                .OldAnnotation("Npgsql:Enum:comportamiento_medio_pago", "cuenta_corriente,efectivo,electronico")
                .OldAnnotation("Npgsql:Enum:estado_tenant", "activo,baja,suspendido")
                .OldAnnotation("Npgsql:Enum:estado_usuario", "activo,bloqueado,inactivo")
                .OldAnnotation("Npgsql:Enum:modo_lista", "derivada,fija")
                .OldAnnotation("Npgsql:Enum:tipo_documento", "cuil,cuit,dni,otro,pasaporte")
                .OldAnnotation("Npgsql:PostgresExtension:citext", ",,");

            migrationBuilder.AddUniqueConstraint(
                name: "ak_proveedores_id_proveedor_id_tenant",
                table: "proveedores",
                columns: new[] { "id_proveedor", "id_tenant" });

            migrationBuilder.AddUniqueConstraint(
                name: "ak_marcas_id_marca_id_tenant",
                table: "marcas",
                columns: new[] { "id_marca", "id_tenant" });

            migrationBuilder.AddUniqueConstraint(
                name: "ak_grupos_id_grupo_id_tenant",
                table: "grupos",
                columns: new[] { "id_grupo", "id_tenant" });

            migrationBuilder.AddUniqueConstraint(
                name: "ak_areas_id_area_id_tenant",
                table: "areas",
                columns: new[] { "id_area", "id_tenant" });

            migrationBuilder.CreateTable(
                name: "articulos",
                columns: table => new
                {
                    id_articulo = table.Column<int>(type: "integer", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    codigo_interno = table.Column<string>(type: "citext", maxLength: 30, nullable: false),
                    nombre = table.Column<string>(type: "citext", maxLength: 150, nullable: false),
                    descripcion = table.Column<string>(type: "text", nullable: true),
                    id_area = table.Column<int>(type: "integer", nullable: false),
                    id_categoria = table.Column<int>(type: "integer", nullable: true),
                    id_marca = table.Column<int>(type: "integer", nullable: true),
                    id_grupo = table.Column<int>(type: "integer", nullable: true),
                    id_proveedor_habitual = table.Column<int>(type: "integer", nullable: true),
                    id_alicuota_iva = table.Column<int>(type: "integer", nullable: false),
                    unidad_venta = table.Column<UnidadVenta>(type: "unidad_venta", nullable: false),
                    unidades_por_bulto = table.Column<decimal>(type: "numeric(10,2)", nullable: true),
                    es_producto = table.Column<bool>(type: "boolean", nullable: false),
                    costo_lista = table.Column<decimal>(type: "numeric(14,2)", nullable: true),
                    descuento_proveedor = table.Column<decimal>(type: "numeric(5,2)", nullable: true),
                    costo_nominal = table.Column<decimal>(type: "numeric(14,2)", nullable: true),
                    disponible_para_todas = table.Column<bool>(type: "boolean", nullable: false, defaultValue: true),
                    activo = table.Column<bool>(type: "boolean", nullable: false, defaultValue: true),
                    created_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    updated_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    deleted_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    id_tenant = table.Column<int>(type: "integer", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_articulos", x => x.id_articulo);
                    table.UniqueConstraint("ak_articulos_id_articulo_id_tenant", x => new { x.id_articulo, x.id_tenant });
                    table.ForeignKey(
                        name: "fk_articulos_alicuota_iva",
                        column: x => x.id_alicuota_iva,
                        principalTable: "alicuotas_iva",
                        principalColumn: "id_alicuota_iva",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "fk_articulos_area",
                        columns: x => new { x.id_area, x.id_tenant },
                        principalTable: "areas",
                        principalColumns: new[] { "id_area", "id_tenant" },
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "fk_articulos_categoria",
                        columns: x => new { x.id_categoria, x.id_tenant },
                        principalTable: "categorias",
                        principalColumns: new[] { "id_categoria", "id_tenant" },
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "fk_articulos_grupo",
                        columns: x => new { x.id_grupo, x.id_tenant },
                        principalTable: "grupos",
                        principalColumns: new[] { "id_grupo", "id_tenant" },
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "fk_articulos_marca",
                        columns: x => new { x.id_marca, x.id_tenant },
                        principalTable: "marcas",
                        principalColumns: new[] { "id_marca", "id_tenant" },
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "fk_articulos_proveedor_habitual",
                        columns: x => new { x.id_proveedor_habitual, x.id_tenant },
                        principalTable: "proveedores",
                        principalColumns: new[] { "id_proveedor", "id_tenant" },
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "fk_articulos_tenant",
                        column: x => x.id_tenant,
                        principalTable: "tenants",
                        principalColumn: "id_tenant",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "numeraciones_articulos",
                columns: table => new
                {
                    id_tenant = table.Column<int>(type: "integer", nullable: false),
                    proximo_numero = table.Column<int>(type: "integer", nullable: false, defaultValue: 1)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_numeraciones_articulos", x => x.id_tenant);
                    table.ForeignKey(
                        name: "fk_numeraciones_articulos_tenant",
                        column: x => x.id_tenant,
                        principalTable: "tenants",
                        principalColumn: "id_tenant",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "articulos_empresas",
                columns: table => new
                {
                    id_articulo = table.Column<int>(type: "integer", nullable: false),
                    id_empresa = table.Column<int>(type: "integer", nullable: false),
                    id_tenant = table.Column<int>(type: "integer", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_articulos_empresas", x => new { x.id_articulo, x.id_empresa });
                    table.ForeignKey(
                        name: "fk_articulos_empresas_articulo",
                        columns: x => new { x.id_articulo, x.id_tenant },
                        principalTable: "articulos",
                        principalColumns: new[] { "id_articulo", "id_tenant" },
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "fk_articulos_empresas_empresa",
                        columns: x => new { x.id_empresa, x.id_tenant },
                        principalTable: "empresas",
                        principalColumns: new[] { "id_empresa", "id_tenant" },
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "fk_articulos_empresas_tenant",
                        column: x => x.id_tenant,
                        principalTable: "tenants",
                        principalColumn: "id_tenant",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "codigos_barra",
                columns: table => new
                {
                    id_codigo_barra = table.Column<int>(type: "integer", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    id_articulo = table.Column<int>(type: "integer", nullable: false),
                    codigo = table.Column<string>(type: "citext", maxLength: 50, nullable: false),
                    activo = table.Column<bool>(type: "boolean", nullable: false, defaultValue: true),
                    created_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    updated_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    deleted_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    id_tenant = table.Column<int>(type: "integer", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_codigos_barra", x => x.id_codigo_barra);
                    table.ForeignKey(
                        name: "fk_codigos_barra_articulo",
                        columns: x => new { x.id_articulo, x.id_tenant },
                        principalTable: "articulos",
                        principalColumns: new[] { "id_articulo", "id_tenant" },
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "fk_codigos_barra_tenant",
                        column: x => x.id_tenant,
                        principalTable: "tenants",
                        principalColumn: "id_tenant",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "precios",
                columns: table => new
                {
                    id_precio = table.Column<int>(type: "integer", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    id_articulo = table.Column<int>(type: "integer", nullable: false),
                    id_lista_precio = table.Column<int>(type: "integer", nullable: false),
                    precio = table.Column<decimal>(type: "numeric(14,2)", nullable: false),
                    vigente_desde = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    vigente_hasta = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    created_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    updated_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    deleted_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    id_tenant = table.Column<int>(type: "integer", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_precios", x => x.id_precio);
                    table.ForeignKey(
                        name: "fk_precios_articulo",
                        columns: x => new { x.id_articulo, x.id_tenant },
                        principalTable: "articulos",
                        principalColumns: new[] { "id_articulo", "id_tenant" },
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "fk_precios_lista_precio",
                        columns: x => new { x.id_lista_precio, x.id_tenant },
                        principalTable: "listas_precio",
                        principalColumns: new[] { "id_lista_precio", "id_tenant" },
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "fk_precios_tenant",
                        column: x => x.id_tenant,
                        principalTable: "tenants",
                        principalColumn: "id_tenant",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateIndex(
                name: "ix_articulos_alicuota_iva",
                table: "articulos",
                column: "id_alicuota_iva");

            migrationBuilder.CreateIndex(
                name: "ix_articulos_area",
                table: "articulos",
                columns: new[] { "id_area", "id_tenant" });

            migrationBuilder.CreateIndex(
                name: "ix_articulos_categoria",
                table: "articulos",
                columns: new[] { "id_categoria", "id_tenant" });

            migrationBuilder.CreateIndex(
                name: "ix_articulos_grupo",
                table: "articulos",
                columns: new[] { "id_grupo", "id_tenant" });

            migrationBuilder.CreateIndex(
                name: "ix_articulos_marca",
                table: "articulos",
                columns: new[] { "id_marca", "id_tenant" });

            migrationBuilder.CreateIndex(
                name: "ix_articulos_proveedor_habitual",
                table: "articulos",
                columns: new[] { "id_proveedor_habitual", "id_tenant" });

            migrationBuilder.CreateIndex(
                name: "ix_articulos_tenant",
                table: "articulos",
                column: "id_tenant");

            migrationBuilder.CreateIndex(
                name: "ux_articulos_codigo_interno",
                table: "articulos",
                columns: new[] { "id_tenant", "codigo_interno" },
                unique: true,
                filter: "deleted_at IS NULL");

            migrationBuilder.CreateIndex(
                name: "ix_articulos_empresas_articulo",
                table: "articulos_empresas",
                columns: new[] { "id_articulo", "id_tenant" });

            migrationBuilder.CreateIndex(
                name: "ix_articulos_empresas_empresa",
                table: "articulos_empresas",
                columns: new[] { "id_empresa", "id_tenant" });

            migrationBuilder.CreateIndex(
                name: "ix_articulos_empresas_tenant",
                table: "articulos_empresas",
                column: "id_tenant");

            migrationBuilder.CreateIndex(
                name: "ix_codigos_barra_articulo",
                table: "codigos_barra",
                columns: new[] { "id_articulo", "id_tenant" });

            migrationBuilder.CreateIndex(
                name: "ix_codigos_barra_tenant",
                table: "codigos_barra",
                column: "id_tenant");

            migrationBuilder.CreateIndex(
                name: "ux_codigos_barra_codigo_tenant",
                table: "codigos_barra",
                columns: new[] { "codigo", "id_tenant" },
                unique: true,
                filter: "deleted_at IS NULL");

            migrationBuilder.CreateIndex(
                name: "ix_precios_articulo",
                table: "precios",
                columns: new[] { "id_articulo", "id_tenant" });

            migrationBuilder.CreateIndex(
                name: "ix_precios_lista_precio",
                table: "precios",
                columns: new[] { "id_lista_precio", "id_tenant" });

            migrationBuilder.CreateIndex(
                name: "ix_precios_tenant",
                table: "precios",
                column: "id_tenant");

            migrationBuilder.CreateIndex(
                name: "ix_precios_vigencia",
                table: "precios",
                columns: new[] { "id_articulo", "id_lista_precio", "vigente_desde" });

            migrationBuilder.CreateIndex(
                name: "ux_precios_vigente",
                table: "precios",
                columns: new[] { "id_articulo", "id_lista_precio" },
                unique: true,
                filter: "vigente_hasta IS NULL AND deleted_at IS NULL");

            // RLS (ADR-4/ADR-15, DB CHANGE GATE aprobado 2026-08-02): las 5 tablas nuevas
            // activan su policy en la misma migración que las crea. app_tenant_actual()/
            // app_modo()/app_es_plataforma() ya existen desde la migración 1 (Organizacion).
            migrationBuilder.HabilitarRlsDeTenant("articulos");
            migrationBuilder.HabilitarRlsDeTenant("articulos_empresas");
            migrationBuilder.HabilitarRlsDeTenant("codigos_barra");
            migrationBuilder.HabilitarRlsDeTenant("numeraciones_articulos");
            migrationBuilder.HabilitarRlsDeTenant("precios");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "articulos_empresas");

            migrationBuilder.DropTable(
                name: "codigos_barra");

            migrationBuilder.DropTable(
                name: "numeraciones_articulos");

            migrationBuilder.DropTable(
                name: "precios");

            migrationBuilder.DropTable(
                name: "articulos");

            migrationBuilder.DropUniqueConstraint(
                name: "ak_proveedores_id_proveedor_id_tenant",
                table: "proveedores");

            migrationBuilder.DropUniqueConstraint(
                name: "ak_marcas_id_marca_id_tenant",
                table: "marcas");

            migrationBuilder.DropUniqueConstraint(
                name: "ak_grupos_id_grupo_id_tenant",
                table: "grupos");

            migrationBuilder.DropUniqueConstraint(
                name: "ak_areas_id_area_id_tenant",
                table: "areas");

            migrationBuilder.AlterDatabase()
                .Annotation("Npgsql:Enum:clase_comprobante", "compra,venta")
                .Annotation("Npgsql:Enum:comportamiento_medio_pago", "cuenta_corriente,efectivo,electronico")
                .Annotation("Npgsql:Enum:estado_tenant", "activo,baja,suspendido")
                .Annotation("Npgsql:Enum:estado_usuario", "activo,bloqueado,inactivo")
                .Annotation("Npgsql:Enum:modo_lista", "derivada,fija")
                .Annotation("Npgsql:Enum:tipo_documento", "cuil,cuit,dni,otro,pasaporte")
                .Annotation("Npgsql:PostgresExtension:citext", ",,")
                .OldAnnotation("Npgsql:Enum:clase_comprobante", "compra,venta")
                .OldAnnotation("Npgsql:Enum:comportamiento_medio_pago", "cuenta_corriente,efectivo,electronico")
                .OldAnnotation("Npgsql:Enum:estado_tenant", "activo,baja,suspendido")
                .OldAnnotation("Npgsql:Enum:estado_usuario", "activo,bloqueado,inactivo")
                .OldAnnotation("Npgsql:Enum:modo_lista", "derivada,fija")
                .OldAnnotation("Npgsql:Enum:tipo_documento", "cuil,cuit,dni,otro,pasaporte")
                .OldAnnotation("Npgsql:Enum:unidad_venta", "peso,unidad")
                .OldAnnotation("Npgsql:PostgresExtension:citext", ",,");
        }
    }
}
