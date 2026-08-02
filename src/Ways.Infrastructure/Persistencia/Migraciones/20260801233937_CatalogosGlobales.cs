using System;
using Microsoft.EntityFrameworkCore.Migrations;
using Npgsql.EntityFrameworkCore.PostgreSQL.Metadata;
using Ways.Domain.Catalogos;
using Ways.Infrastructure.Multitenancy;

#nullable disable

namespace Ways.Infrastructure.Persistencia.Migraciones
{
    /// <inheritdoc />
    public partial class CatalogosGlobales : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AlterDatabase()
                .Annotation("Npgsql:Enum:clase_comprobante", "compra,venta")
                .Annotation("Npgsql:Enum:comportamiento_medio_pago", "cuenta_corriente,efectivo,electronico")
                .Annotation("Npgsql:Enum:estado_tenant", "activo,baja,suspendido")
                .Annotation("Npgsql:Enum:estado_usuario", "activo,bloqueado,inactivo")
                .Annotation("Npgsql:PostgresExtension:citext", ",,")
                .OldAnnotation("Npgsql:Enum:comportamiento_medio_pago", "cuenta_corriente,efectivo,electronico")
                .OldAnnotation("Npgsql:Enum:estado_tenant", "activo,baja,suspendido")
                .OldAnnotation("Npgsql:Enum:estado_usuario", "activo,bloqueado,inactivo")
                .OldAnnotation("Npgsql:PostgresExtension:citext", ",,");

            migrationBuilder.CreateTable(
                name: "alicuotas_iva",
                columns: table => new
                {
                    id_alicuota_iva = table.Column<int>(type: "integer", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    nombre = table.Column<string>(type: "citext", maxLength: 30, nullable: false),
                    porcentaje = table.Column<decimal>(type: "numeric(5,2)", nullable: false),
                    codigo_afip = table.Column<short>(type: "smallint", nullable: true),
                    activo = table.Column<bool>(type: "boolean", nullable: false, defaultValue: true),
                    created_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    updated_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    deleted_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_alicuotas_iva", x => x.id_alicuota_iva);
                });

            migrationBuilder.CreateTable(
                name: "condiciones_fiscales",
                columns: table => new
                {
                    id_condicion_fiscal = table.Column<int>(type: "integer", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    codigo = table.Column<string>(type: "citext", maxLength: 30, nullable: false),
                    nombre = table.Column<string>(type: "citext", maxLength: 100, nullable: false),
                    codigo_afip = table.Column<short>(type: "smallint", nullable: true),
                    activo = table.Column<bool>(type: "boolean", nullable: false, defaultValue: true),
                    created_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    updated_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    deleted_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_condiciones_fiscales", x => x.id_condicion_fiscal);
                });

            migrationBuilder.CreateTable(
                name: "tipos_comprobante",
                columns: table => new
                {
                    id_tipo_comprobante = table.Column<int>(type: "integer", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    clase = table.Column<ClaseComprobante>(type: "clase_comprobante", nullable: false),
                    codigo = table.Column<string>(type: "citext", maxLength: 10, nullable: false),
                    nombre = table.Column<string>(type: "citext", maxLength: 100, nullable: false),
                    letra = table.Column<char>(type: "char(1)", nullable: true),
                    signo = table.Column<short>(type: "smallint", nullable: false),
                    discrimina_iva = table.Column<bool>(type: "boolean", nullable: false),
                    es_fiscal = table.Column<bool>(type: "boolean", nullable: false),
                    afecta_stock = table.Column<bool>(type: "boolean", nullable: false),
                    codigo_afip = table.Column<short>(type: "smallint", nullable: true),
                    activo = table.Column<bool>(type: "boolean", nullable: false, defaultValue: true),
                    created_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    updated_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    deleted_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_tipos_comprobante", x => x.id_tipo_comprobante);
                });

            migrationBuilder.CreateIndex(
                name: "ux_alicuotas_iva_nombre",
                table: "alicuotas_iva",
                column: "nombre",
                unique: true,
                filter: "deleted_at IS NULL");

            migrationBuilder.CreateIndex(
                name: "ux_condiciones_fiscales_codigo",
                table: "condiciones_fiscales",
                column: "codigo",
                unique: true,
                filter: "deleted_at IS NULL");

            migrationBuilder.CreateIndex(
                name: "ux_tipos_comprobante_codigo",
                table: "tipos_comprobante",
                column: "codigo",
                unique: true,
                filter: "deleted_at IS NULL");

            // RLS (override de ADR-11, decisión del usuario 2026-08-01, DB CHANGE GATE #4):
            // dato de referencia global, legible en cualquier modo de acceso, escritura
            // restringida a la plataforma. No requiere que las funciones de contexto
            // (app_tenant_actual/app_modo/app_es_plataforma, migración 1) hagan nada distinto
            // — solo usa app_es_plataforma().
            migrationBuilder.HabilitarRlsDeCatalogoGlobal("condiciones_fiscales");
            migrationBuilder.HabilitarRlsDeCatalogoGlobal("alicuotas_iva");
            migrationBuilder.HabilitarRlsDeCatalogoGlobal("tipos_comprobante");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "alicuotas_iva");

            migrationBuilder.DropTable(
                name: "condiciones_fiscales");

            migrationBuilder.DropTable(
                name: "tipos_comprobante");

            migrationBuilder.AlterDatabase()
                .Annotation("Npgsql:Enum:comportamiento_medio_pago", "cuenta_corriente,efectivo,electronico")
                .Annotation("Npgsql:Enum:estado_tenant", "activo,baja,suspendido")
                .Annotation("Npgsql:Enum:estado_usuario", "activo,bloqueado,inactivo")
                .Annotation("Npgsql:PostgresExtension:citext", ",,")
                .OldAnnotation("Npgsql:Enum:clase_comprobante", "compra,venta")
                .OldAnnotation("Npgsql:Enum:comportamiento_medio_pago", "cuenta_corriente,efectivo,electronico")
                .OldAnnotation("Npgsql:Enum:estado_tenant", "activo,baja,suspendido")
                .OldAnnotation("Npgsql:Enum:estado_usuario", "activo,bloqueado,inactivo")
                .OldAnnotation("Npgsql:PostgresExtension:citext", ",,");
        }
    }
}
