using System;
using Microsoft.EntityFrameworkCore.Migrations;
using Npgsql.EntityFrameworkCore.PostgreSQL.Metadata;
using Ways.Domain.Organizacion;
using Ways.Infrastructure.Multitenancy;

#nullable disable

namespace Ways.Infrastructure.Persistencia.Migraciones
{
    /// <inheritdoc />
    public partial class Organizacion : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AlterDatabase()
                .Annotation("Npgsql:Enum:estado_tenant", "activo,baja,suspendido")
                .Annotation("Npgsql:Enum:estado_usuario", "activo,bloqueado,inactivo")
                .Annotation("Npgsql:PostgresExtension:citext", ",,")
                .OldAnnotation("Npgsql:Enum:estado_usuario", "activo,bloqueado,inactivo")
                .OldAnnotation("Npgsql:PostgresExtension:citext", ",,");

            migrationBuilder.CreateTable(
                name: "tenants",
                columns: table => new
                {
                    id_tenant = table.Column<int>(type: "integer", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    nombre = table.Column<string>(type: "citext", maxLength: 150, nullable: false),
                    estado = table.Column<EstadoTenant>(type: "estado_tenant", nullable: false, defaultValue: EstadoTenant.Activo),
                    created_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    updated_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    deleted_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_tenants", x => x.id_tenant);
                });

            migrationBuilder.CreateTable(
                name: "empresas",
                columns: table => new
                {
                    id_empresa = table.Column<int>(type: "integer", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    razon_social = table.Column<string>(type: "citext", maxLength: 150, nullable: false),
                    nombre_fantasia = table.Column<string>(type: "citext", maxLength: 150, nullable: true),
                    cuit = table.Column<string>(type: "character varying(13)", maxLength: 13, nullable: true),
                    created_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    updated_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    deleted_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    id_tenant = table.Column<int>(type: "integer", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_empresas", x => x.id_empresa);
                    table.UniqueConstraint("ak_empresas_id_empresa_id_tenant", x => new { x.id_empresa, x.id_tenant });
                    table.ForeignKey(
                        name: "fk_empresas_tenant",
                        column: x => x.id_tenant,
                        principalTable: "tenants",
                        principalColumn: "id_tenant",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "puntos_venta",
                columns: table => new
                {
                    id_punto_venta = table.Column<int>(type: "integer", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    id_empresa = table.Column<int>(type: "integer", nullable: false),
                    nombre = table.Column<string>(type: "citext", maxLength: 150, nullable: false),
                    domicilio = table.Column<string>(type: "character varying(255)", maxLength: 255, nullable: true),
                    horario = table.Column<string>(type: "character varying(255)", maxLength: 255, nullable: true),
                    whatsapp = table.Column<string>(type: "character varying(30)", maxLength: 30, nullable: true),
                    instagram = table.Column<string>(type: "character varying(150)", maxLength: 150, nullable: true),
                    facebook = table.Column<string>(type: "character varying(150)", maxLength: 150, nullable: true),
                    web = table.Column<string>(type: "character varying(255)", maxLength: 255, nullable: true),
                    created_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    updated_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    deleted_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    id_tenant = table.Column<int>(type: "integer", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_puntos_venta", x => x.id_punto_venta);
                    table.UniqueConstraint("ak_puntos_venta_id_punto_venta_id_tenant", x => new { x.id_punto_venta, x.id_tenant });
                    table.ForeignKey(
                        name: "fk_puntos_venta_empresa",
                        columns: x => new { x.id_empresa, x.id_tenant },
                        principalTable: "empresas",
                        principalColumns: new[] { "id_empresa", "id_tenant" },
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateIndex(
                name: "ix_empresas_tenant",
                table: "empresas",
                column: "id_tenant");

            migrationBuilder.CreateIndex(
                name: "ix_puntos_venta_empresa",
                table: "puntos_venta",
                columns: new[] { "id_empresa", "id_tenant" });

            migrationBuilder.CreateIndex(
                name: "ix_puntos_venta_tenant",
                table: "puntos_venta",
                column: "id_tenant");

            // --- Aislamiento por RLS (doc 09, ADR-4, ADR-5, ADR-15) ---
            // Las funciones se crean una sola vez acá; el resto de las tablas scopeadas
            // (slices siguientes) solo llaman a HabilitarRlsDeTenant.
            migrationBuilder.CrearFuncionesDeContextoDeTenant();

            // tenants: su propia PK (columna id_tenant) ES el alcance — el mismo patrón
            // de policy aplica literal, porque la columna se llama id_tenant.
            migrationBuilder.HabilitarRlsDeTenant("tenants");
            migrationBuilder.HabilitarRlsDeTenant("empresas");
            migrationBuilder.HabilitarRlsDeTenant("puntos_venta");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            // DROP TABLE se lleva puestas sus policies solo; las funciones sobreviven a
            // las tablas y se dropean después, para no toparse con dependencias.
            migrationBuilder.DropTable(
                name: "puntos_venta");

            migrationBuilder.DropTable(
                name: "empresas");

            migrationBuilder.DropTable(
                name: "tenants");

            migrationBuilder.Sql("DROP FUNCTION IF EXISTS app_es_plataforma();");
            migrationBuilder.Sql("DROP FUNCTION IF EXISTS app_modo();");
            migrationBuilder.Sql("DROP FUNCTION IF EXISTS app_tenant_actual();");

            migrationBuilder.AlterDatabase()
                .Annotation("Npgsql:Enum:estado_usuario", "activo,bloqueado,inactivo")
                .Annotation("Npgsql:PostgresExtension:citext", ",,")
                .OldAnnotation("Npgsql:Enum:estado_tenant", "activo,baja,suspendido")
                .OldAnnotation("Npgsql:Enum:estado_usuario", "activo,bloqueado,inactivo")
                .OldAnnotation("Npgsql:PostgresExtension:citext", ",,");
        }
    }
}
