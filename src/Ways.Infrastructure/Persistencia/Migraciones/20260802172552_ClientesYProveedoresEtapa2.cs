using System;
using Microsoft.EntityFrameworkCore.Migrations;
using Npgsql.EntityFrameworkCore.PostgreSQL.Metadata;
using Ways.Domain.Catalogos;
using Ways.Domain.Clientes;
using Ways.Infrastructure.Multitenancy;

#nullable disable

namespace Ways.Infrastructure.Persistencia.Migraciones
{
    /// <inheritdoc />
    public partial class ClientesYProveedoresEtapa2 : Migration
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
                .Annotation("Npgsql:PostgresExtension:citext", ",,")
                .OldAnnotation("Npgsql:Enum:clase_comprobante", "compra,venta")
                .OldAnnotation("Npgsql:Enum:comportamiento_medio_pago", "cuenta_corriente,efectivo,electronico")
                .OldAnnotation("Npgsql:Enum:estado_tenant", "activo,baja,suspendido")
                .OldAnnotation("Npgsql:Enum:estado_usuario", "activo,bloqueado,inactivo")
                .OldAnnotation("Npgsql:PostgresExtension:citext", ",,");

            migrationBuilder.CreateTable(
                name: "listas_precio",
                columns: table => new
                {
                    id_lista_precio = table.Column<int>(type: "integer", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    es_default = table.Column<bool>(type: "boolean", nullable: false),
                    modo = table.Column<ModoLista>(type: "modo_lista", nullable: false, defaultValue: ModoLista.Fija),
                    id_lista_base = table.Column<int>(type: "integer", nullable: true),
                    porcentaje = table.Column<decimal>(type: "numeric(5,2)", nullable: true),
                    created_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    updated_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    deleted_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    id_tenant = table.Column<int>(type: "integer", nullable: false),
                    nombre = table.Column<string>(type: "citext", maxLength: 150, nullable: false),
                    activo = table.Column<bool>(type: "boolean", nullable: false, defaultValue: true),
                    id_empresa = table.Column<int>(type: "integer", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_listas_precio", x => x.id_lista_precio);
                    table.UniqueConstraint("ak_listas_precio_id_tenant", x => new { x.id_lista_precio, x.id_tenant });
                    table.ForeignKey(
                        name: "fk_listas_precio_empresa",
                        columns: x => new { x.id_empresa, x.id_tenant },
                        principalTable: "empresas",
                        principalColumns: new[] { "id_empresa", "id_tenant" },
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "fk_listas_precio_lista_base",
                        column: x => x.id_lista_base,
                        principalTable: "listas_precio",
                        principalColumn: "id_lista_precio",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "fk_listas_precio_tenant",
                        column: x => x.id_tenant,
                        principalTable: "tenants",
                        principalColumn: "id_tenant",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "numeraciones_clientes",
                columns: table => new
                {
                    id_tenant = table.Column<int>(type: "integer", nullable: false),
                    proximo_numero = table.Column<int>(type: "integer", nullable: false, defaultValue: 1)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_numeraciones_clientes", x => x.id_tenant);
                    table.ForeignKey(
                        name: "fk_numeraciones_clientes_tenant",
                        column: x => x.id_tenant,
                        principalTable: "tenants",
                        principalColumn: "id_tenant",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "proveedores",
                columns: table => new
                {
                    id_proveedor = table.Column<int>(type: "integer", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    id_empresa = table.Column<int>(type: "integer", nullable: true),
                    razon_social = table.Column<string>(type: "citext", maxLength: 150, nullable: false),
                    nombre_fantasia = table.Column<string>(type: "citext", maxLength: 150, nullable: true),
                    cuit = table.Column<string>(type: "character varying(13)", maxLength: 13, nullable: true),
                    id_condicion_fiscal = table.Column<int>(type: "integer", nullable: false),
                    domicilio = table.Column<string>(type: "citext", maxLength: 255, nullable: true),
                    telefono = table.Column<string>(type: "citext", maxLength: 50, nullable: true),
                    email = table.Column<string>(type: "citext", maxLength: 255, nullable: true),
                    vendedor = table.Column<string>(type: "citext", maxLength: 150, nullable: true),
                    celular_vendedor = table.Column<string>(type: "citext", maxLength: 50, nullable: true),
                    supervisor = table.Column<string>(type: "citext", maxLength: 150, nullable: true),
                    celular_supervisor = table.Column<string>(type: "citext", maxLength: 50, nullable: true),
                    margen = table.Column<decimal>(type: "numeric(5,2)", nullable: true),
                    observaciones = table.Column<string>(type: "text", nullable: true),
                    activo = table.Column<bool>(type: "boolean", nullable: false, defaultValue: true),
                    created_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    updated_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    deleted_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    id_tenant = table.Column<int>(type: "integer", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_proveedores", x => x.id_proveedor);
                    table.ForeignKey(
                        name: "fk_proveedores_condicion_fiscal",
                        column: x => x.id_condicion_fiscal,
                        principalTable: "condiciones_fiscales",
                        principalColumn: "id_condicion_fiscal",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "fk_proveedores_empresa",
                        columns: x => new { x.id_empresa, x.id_tenant },
                        principalTable: "empresas",
                        principalColumns: new[] { "id_empresa", "id_tenant" },
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "fk_proveedores_tenant",
                        column: x => x.id_tenant,
                        principalTable: "tenants",
                        principalColumn: "id_tenant",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "clientes",
                columns: table => new
                {
                    id_cliente = table.Column<int>(type: "integer", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    id_empresa = table.Column<int>(type: "integer", nullable: true),
                    numero = table.Column<int>(type: "integer", nullable: false),
                    nombre = table.Column<string>(type: "citext", maxLength: 150, nullable: false),
                    apellido = table.Column<string>(type: "citext", maxLength: 150, nullable: true),
                    razon_social = table.Column<string>(type: "citext", maxLength: 150, nullable: true),
                    tipo_documento = table.Column<TipoDocumento>(type: "tipo_documento", nullable: true),
                    numero_documento = table.Column<string>(type: "citext", maxLength: 30, nullable: true),
                    id_condicion_fiscal = table.Column<int>(type: "integer", nullable: false),
                    nacimiento = table.Column<DateOnly>(type: "date", nullable: true),
                    domicilio = table.Column<string>(type: "citext", maxLength: 255, nullable: true),
                    telefono = table.Column<string>(type: "citext", maxLength: 50, nullable: true),
                    celular = table.Column<string>(type: "citext", maxLength: 50, nullable: true),
                    email = table.Column<string>(type: "citext", maxLength: 255, nullable: true),
                    observaciones = table.Column<string>(type: "text", nullable: true),
                    id_lista_precio = table.Column<int>(type: "integer", nullable: false),
                    limite_credito = table.Column<decimal>(type: "numeric(14,2)", nullable: false, defaultValue: 0m),
                    credito_ilimitado = table.Column<bool>(type: "boolean", nullable: false, defaultValue: false),
                    saldo = table.Column<decimal>(type: "numeric(14,2)", nullable: false, defaultValue: 0m),
                    activo = table.Column<bool>(type: "boolean", nullable: false, defaultValue: true),
                    created_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    updated_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    deleted_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    id_tenant = table.Column<int>(type: "integer", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_clientes", x => x.id_cliente);
                    table.CheckConstraint("ck_clientes_cf_protegido", "numero <> 1 OR deleted_at IS NULL");
                    table.ForeignKey(
                        name: "fk_clientes_condicion_fiscal",
                        column: x => x.id_condicion_fiscal,
                        principalTable: "condiciones_fiscales",
                        principalColumn: "id_condicion_fiscal",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "fk_clientes_empresa",
                        columns: x => new { x.id_empresa, x.id_tenant },
                        principalTable: "empresas",
                        principalColumns: new[] { "id_empresa", "id_tenant" },
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "fk_clientes_lista_precio",
                        columns: x => new { x.id_lista_precio, x.id_tenant },
                        principalTable: "listas_precio",
                        principalColumns: new[] { "id_lista_precio", "id_tenant" },
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "fk_clientes_tenant",
                        column: x => x.id_tenant,
                        principalTable: "tenants",
                        principalColumn: "id_tenant",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateIndex(
                name: "ix_clientes_condicion_fiscal",
                table: "clientes",
                column: "id_condicion_fiscal");

            migrationBuilder.CreateIndex(
                name: "ix_clientes_empresa",
                table: "clientes",
                columns: new[] { "id_empresa", "id_tenant" });

            migrationBuilder.CreateIndex(
                name: "ix_clientes_lista_precio",
                table: "clientes",
                columns: new[] { "id_lista_precio", "id_tenant" });

            migrationBuilder.CreateIndex(
                name: "ix_clientes_tenant",
                table: "clientes",
                column: "id_tenant");

            migrationBuilder.CreateIndex(
                name: "ux_clientes_numero",
                table: "clientes",
                columns: new[] { "id_tenant", "numero" },
                unique: true,
                filter: "deleted_at IS NULL");

            migrationBuilder.CreateIndex(
                name: "ix_listas_precio_empresa",
                table: "listas_precio",
                columns: new[] { "id_empresa", "id_tenant" });

            migrationBuilder.CreateIndex(
                name: "ix_listas_precio_lista_base",
                table: "listas_precio",
                column: "id_lista_base");

            migrationBuilder.CreateIndex(
                name: "ix_listas_precio_tenant",
                table: "listas_precio",
                column: "id_tenant");

            migrationBuilder.CreateIndex(
                name: "ux_listas_precio_default_compartido",
                table: "listas_precio",
                columns: new[] { "id_tenant", "es_default" },
                unique: true,
                filter: "id_empresa IS NULL AND deleted_at IS NULL AND es_default = true");

            migrationBuilder.CreateIndex(
                name: "ux_listas_precio_default_empresa",
                table: "listas_precio",
                columns: new[] { "id_tenant", "id_empresa", "es_default" },
                unique: true,
                filter: "id_empresa IS NOT NULL AND deleted_at IS NULL AND es_default = true");

            migrationBuilder.CreateIndex(
                name: "ux_listas_precio_nombre_compartido",
                table: "listas_precio",
                columns: new[] { "id_tenant", "nombre" },
                unique: true,
                filter: "id_empresa IS NULL AND deleted_at IS NULL");

            migrationBuilder.CreateIndex(
                name: "ux_listas_precio_nombre_empresa",
                table: "listas_precio",
                columns: new[] { "id_tenant", "id_empresa", "nombre" },
                unique: true,
                filter: "id_empresa IS NOT NULL AND deleted_at IS NULL");

            migrationBuilder.CreateIndex(
                name: "ix_proveedores_condicion_fiscal",
                table: "proveedores",
                column: "id_condicion_fiscal");

            migrationBuilder.CreateIndex(
                name: "ix_proveedores_empresa",
                table: "proveedores",
                columns: new[] { "id_empresa", "id_tenant" });

            migrationBuilder.CreateIndex(
                name: "ix_proveedores_tenant",
                table: "proveedores",
                column: "id_tenant");

            migrationBuilder.CreateIndex(
                name: "ux_proveedores_cuit",
                table: "proveedores",
                columns: new[] { "id_tenant", "cuit" },
                unique: true,
                filter: "deleted_at IS NULL AND cuit IS NOT NULL");

            // RLS (ADR-4/ADR-15, DB CHANGE GATE aprobado 2026-08-02): las 4 tablas nuevas
            // activan su policy en la misma migración que las crea. app_tenant_actual()/
            // app_modo()/app_es_plataforma() ya existen desde la migración 1 (Organizacion).
            migrationBuilder.HabilitarRlsDeTenant("listas_precio");
            migrationBuilder.HabilitarRlsDeTenant("numeraciones_clientes");
            migrationBuilder.HabilitarRlsDeTenant("proveedores");
            migrationBuilder.HabilitarRlsDeTenant("clientes");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "clientes");

            migrationBuilder.DropTable(
                name: "numeraciones_clientes");

            migrationBuilder.DropTable(
                name: "proveedores");

            migrationBuilder.DropTable(
                name: "listas_precio");

            migrationBuilder.AlterDatabase()
                .Annotation("Npgsql:Enum:clase_comprobante", "compra,venta")
                .Annotation("Npgsql:Enum:comportamiento_medio_pago", "cuenta_corriente,efectivo,electronico")
                .Annotation("Npgsql:Enum:estado_tenant", "activo,baja,suspendido")
                .Annotation("Npgsql:Enum:estado_usuario", "activo,bloqueado,inactivo")
                .Annotation("Npgsql:PostgresExtension:citext", ",,")
                .OldAnnotation("Npgsql:Enum:clase_comprobante", "compra,venta")
                .OldAnnotation("Npgsql:Enum:comportamiento_medio_pago", "cuenta_corriente,efectivo,electronico")
                .OldAnnotation("Npgsql:Enum:estado_tenant", "activo,baja,suspendido")
                .OldAnnotation("Npgsql:Enum:estado_usuario", "activo,bloqueado,inactivo")
                .OldAnnotation("Npgsql:Enum:modo_lista", "derivada,fija")
                .OldAnnotation("Npgsql:Enum:tipo_documento", "cuil,cuit,dni,otro,pasaporte")
                .OldAnnotation("Npgsql:PostgresExtension:citext", ",,");
        }
    }
}
