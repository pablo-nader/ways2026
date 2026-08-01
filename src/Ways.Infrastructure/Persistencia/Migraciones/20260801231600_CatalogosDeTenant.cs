using System;
using Microsoft.EntityFrameworkCore.Migrations;
using Npgsql.EntityFrameworkCore.PostgreSQL.Metadata;
using Ways.Domain.Catalogos;
using Ways.Infrastructure.Multitenancy;

#nullable disable

namespace Ways.Infrastructure.Persistencia.Migraciones
{
    /// <inheritdoc />
    public partial class CatalogosDeTenant : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            // NOTA: "clase_comprobante" NO se crea acá — es del gate #4 (CatalogosGlobales,
            // tipos_comprobante), todavía sin aprobar; "parametros" tampoco está acá — es su
            // propia migración, gate #5. El scaffolder de `dotnet ef` los mezcla igual porque
            // WaysDbContextFactory mapea todos los enums conocidos por MapEnum<T>() en tiempo
            // de diseño, y ApplyConfigurationsFromAssembly registra toda IEntityTypeConfiguration
            // del assembly, sin importar el corte de gates que design.md pide — se excluyen a
            // mano (Ignore<T>() temporal en WaysDbContext.OnModelCreating durante el scaffold,
            // ya revertido) para que esta migración sea exactamente lo que el gate #3 aprobó.
            migrationBuilder.AlterDatabase()
                .Annotation("Npgsql:Enum:comportamiento_medio_pago", "cuenta_corriente,efectivo,electronico")
                .Annotation("Npgsql:Enum:estado_tenant", "activo,baja,suspendido")
                .Annotation("Npgsql:Enum:estado_usuario", "activo,bloqueado,inactivo")
                .Annotation("Npgsql:PostgresExtension:citext", ",,")
                .OldAnnotation("Npgsql:Enum:estado_tenant", "activo,baja,suspendido")
                .OldAnnotation("Npgsql:Enum:estado_usuario", "activo,bloqueado,inactivo")
                .OldAnnotation("Npgsql:PostgresExtension:citext", ",,");

            migrationBuilder.CreateTable(
                name: "areas",
                columns: table => new
                {
                    id_area = table.Column<int>(type: "integer", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    orden = table.Column<int>(type: "integer", nullable: false),
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
                    table.PrimaryKey("PK_areas", x => x.id_area);
                    table.ForeignKey(
                        name: "fk_areas_empresa",
                        columns: x => new { x.id_empresa, x.id_tenant },
                        principalTable: "empresas",
                        principalColumns: new[] { "id_empresa", "id_tenant" },
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "fk_areas_tenant",
                        column: x => x.id_tenant,
                        principalTable: "tenants",
                        principalColumn: "id_tenant",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "categorias",
                columns: table => new
                {
                    id_categoria = table.Column<int>(type: "integer", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    orden = table.Column<int>(type: "integer", nullable: false),
                    id_categoria_padre = table.Column<int>(type: "integer", nullable: true),
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
                    table.PrimaryKey("PK_categorias", x => x.id_categoria);
                    table.UniqueConstraint("ak_categorias_id_categoria_id_tenant", x => new { x.id_categoria, x.id_tenant });
                    table.ForeignKey(
                        name: "fk_categorias_empresa",
                        columns: x => new { x.id_empresa, x.id_tenant },
                        principalTable: "empresas",
                        principalColumns: new[] { "id_empresa", "id_tenant" },
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "fk_categorias_padre",
                        columns: x => new { x.id_categoria_padre, x.id_tenant },
                        principalTable: "categorias",
                        principalColumns: new[] { "id_categoria", "id_tenant" },
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "fk_categorias_tenant",
                        column: x => x.id_tenant,
                        principalTable: "tenants",
                        principalColumn: "id_tenant",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "grupos",
                columns: table => new
                {
                    id_grupo = table.Column<int>(type: "integer", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    margen = table.Column<decimal>(type: "numeric(5,2)", nullable: true),
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
                    table.PrimaryKey("PK_grupos", x => x.id_grupo);
                    table.ForeignKey(
                        name: "fk_grupos_empresa",
                        columns: x => new { x.id_empresa, x.id_tenant },
                        principalTable: "empresas",
                        principalColumns: new[] { "id_empresa", "id_tenant" },
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "fk_grupos_tenant",
                        column: x => x.id_tenant,
                        principalTable: "tenants",
                        principalColumn: "id_tenant",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "marcas",
                columns: table => new
                {
                    id_marca = table.Column<int>(type: "integer", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
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
                    table.PrimaryKey("PK_marcas", x => x.id_marca);
                    table.ForeignKey(
                        name: "fk_marcas_empresa",
                        columns: x => new { x.id_empresa, x.id_tenant },
                        principalTable: "empresas",
                        principalColumns: new[] { "id_empresa", "id_tenant" },
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "fk_marcas_tenant",
                        column: x => x.id_tenant,
                        principalTable: "tenants",
                        principalColumn: "id_tenant",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "medios_pago",
                columns: table => new
                {
                    id_medio_pago = table.Column<int>(type: "integer", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    orden = table.Column<int>(type: "integer", nullable: false),
                    comportamiento = table.Column<ComportamientoMedioPago>(type: "comportamiento_medio_pago", nullable: false),
                    admite_vuelto = table.Column<bool>(type: "boolean", nullable: false),
                    requiere_referencia = table.Column<bool>(type: "boolean", nullable: false),
                    recargo_porcentaje = table.Column<decimal>(type: "numeric(5,2)", nullable: true),
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
                    table.PrimaryKey("PK_medios_pago", x => x.id_medio_pago);
                    table.ForeignKey(
                        name: "fk_medios_pago_empresa",
                        columns: x => new { x.id_empresa, x.id_tenant },
                        principalTable: "empresas",
                        principalColumns: new[] { "id_empresa", "id_tenant" },
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "fk_medios_pago_tenant",
                        column: x => x.id_tenant,
                        principalTable: "tenants",
                        principalColumn: "id_tenant",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateIndex(
                name: "ix_areas_empresa",
                table: "areas",
                columns: new[] { "id_empresa", "id_tenant" });

            migrationBuilder.CreateIndex(
                name: "ix_areas_tenant",
                table: "areas",
                column: "id_tenant");

            migrationBuilder.CreateIndex(
                name: "ux_areas_nombre_compartido",
                table: "areas",
                columns: new[] { "id_tenant", "nombre" },
                unique: true,
                filter: "id_empresa IS NULL AND deleted_at IS NULL");

            migrationBuilder.CreateIndex(
                name: "ux_areas_nombre_empresa",
                table: "areas",
                columns: new[] { "id_tenant", "id_empresa", "nombre" },
                unique: true,
                filter: "id_empresa IS NOT NULL AND deleted_at IS NULL");

            migrationBuilder.CreateIndex(
                name: "ix_categorias_empresa",
                table: "categorias",
                columns: new[] { "id_empresa", "id_tenant" });

            migrationBuilder.CreateIndex(
                name: "ix_categorias_padre",
                table: "categorias",
                columns: new[] { "id_categoria_padre", "id_tenant" });

            migrationBuilder.CreateIndex(
                name: "ix_categorias_tenant",
                table: "categorias",
                column: "id_tenant");

            migrationBuilder.CreateIndex(
                name: "ux_categorias_nombre_compartido",
                table: "categorias",
                columns: new[] { "id_tenant", "nombre" },
                unique: true,
                filter: "id_empresa IS NULL AND deleted_at IS NULL");

            migrationBuilder.CreateIndex(
                name: "ux_categorias_nombre_empresa",
                table: "categorias",
                columns: new[] { "id_tenant", "id_empresa", "nombre" },
                unique: true,
                filter: "id_empresa IS NOT NULL AND deleted_at IS NULL");

            migrationBuilder.CreateIndex(
                name: "ix_grupos_empresa",
                table: "grupos",
                columns: new[] { "id_empresa", "id_tenant" });

            migrationBuilder.CreateIndex(
                name: "ix_grupos_tenant",
                table: "grupos",
                column: "id_tenant");

            migrationBuilder.CreateIndex(
                name: "ux_grupos_nombre_compartido",
                table: "grupos",
                columns: new[] { "id_tenant", "nombre" },
                unique: true,
                filter: "id_empresa IS NULL AND deleted_at IS NULL");

            migrationBuilder.CreateIndex(
                name: "ux_grupos_nombre_empresa",
                table: "grupos",
                columns: new[] { "id_tenant", "id_empresa", "nombre" },
                unique: true,
                filter: "id_empresa IS NOT NULL AND deleted_at IS NULL");

            migrationBuilder.CreateIndex(
                name: "ix_marcas_empresa",
                table: "marcas",
                columns: new[] { "id_empresa", "id_tenant" });

            migrationBuilder.CreateIndex(
                name: "ix_marcas_tenant",
                table: "marcas",
                column: "id_tenant");

            migrationBuilder.CreateIndex(
                name: "ux_marcas_nombre_compartido",
                table: "marcas",
                columns: new[] { "id_tenant", "nombre" },
                unique: true,
                filter: "id_empresa IS NULL AND deleted_at IS NULL");

            migrationBuilder.CreateIndex(
                name: "ux_marcas_nombre_empresa",
                table: "marcas",
                columns: new[] { "id_tenant", "id_empresa", "nombre" },
                unique: true,
                filter: "id_empresa IS NOT NULL AND deleted_at IS NULL");

            migrationBuilder.CreateIndex(
                name: "ix_medios_pago_empresa",
                table: "medios_pago",
                columns: new[] { "id_empresa", "id_tenant" });

            migrationBuilder.CreateIndex(
                name: "ix_medios_pago_tenant",
                table: "medios_pago",
                column: "id_tenant");

            migrationBuilder.CreateIndex(
                name: "ux_medios_pago_nombre_compartido",
                table: "medios_pago",
                columns: new[] { "id_tenant", "nombre" },
                unique: true,
                filter: "id_empresa IS NULL AND deleted_at IS NULL");

            migrationBuilder.CreateIndex(
                name: "ux_medios_pago_nombre_empresa",
                table: "medios_pago",
                columns: new[] { "id_tenant", "id_empresa", "nombre" },
                unique: true,
                filter: "id_empresa IS NOT NULL AND deleted_at IS NULL");

            // RLS (ADR-4/ADR-15): cada tabla scopeada activa su policy en la misma migración
            // que la crea. Las funciones app_tenant_actual()/app_modo()/app_es_plataforma()
            // ya existen desde la migración 1 (Organizacion). El guard de identificador de
            // HabilitarRlsDeTenant (slice 3, judgment-day INFO) es lo que hace seguro
            // interpolar estos 5 nombres de tabla.
            migrationBuilder.HabilitarRlsDeTenant("areas");
            migrationBuilder.HabilitarRlsDeTenant("categorias");
            migrationBuilder.HabilitarRlsDeTenant("marcas");
            migrationBuilder.HabilitarRlsDeTenant("grupos");
            migrationBuilder.HabilitarRlsDeTenant("medios_pago");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "areas");

            migrationBuilder.DropTable(
                name: "categorias");

            migrationBuilder.DropTable(
                name: "grupos");

            migrationBuilder.DropTable(
                name: "marcas");

            migrationBuilder.DropTable(
                name: "medios_pago");

            migrationBuilder.AlterDatabase()
                .Annotation("Npgsql:Enum:estado_tenant", "activo,baja,suspendido")
                .Annotation("Npgsql:Enum:estado_usuario", "activo,bloqueado,inactivo")
                .Annotation("Npgsql:PostgresExtension:citext", ",,")
                .OldAnnotation("Npgsql:Enum:comportamiento_medio_pago", "cuenta_corriente,efectivo,electronico")
                .OldAnnotation("Npgsql:Enum:estado_tenant", "activo,baja,suspendido")
                .OldAnnotation("Npgsql:Enum:estado_usuario", "activo,bloqueado,inactivo")
                .OldAnnotation("Npgsql:PostgresExtension:citext", ",,");
        }
    }
}
