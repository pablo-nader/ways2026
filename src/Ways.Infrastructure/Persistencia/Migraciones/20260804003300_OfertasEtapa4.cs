using System;
using Microsoft.EntityFrameworkCore.Migrations;
using Npgsql.EntityFrameworkCore.PostgreSQL.Metadata;
using Ways.Infrastructure.Multitenancy;

#nullable disable

namespace Ways.Infrastructure.Persistencia.Migraciones
{
    /// <inheritdoc />
    public partial class OfertasEtapa4 : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "ofertas",
                columns: table => new
                {
                    id_oferta = table.Column<int>(type: "integer", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    id_empresa = table.Column<int>(type: "integer", nullable: true),
                    nombre = table.Column<string>(type: "citext", maxLength: 150, nullable: false),
                    id_articulo = table.Column<int>(type: "integer", nullable: true),
                    id_grupo = table.Column<int>(type: "integer", nullable: true),
                    id_categoria = table.Column<int>(type: "integer", nullable: true),
                    fecha_desde = table.Column<DateOnly>(type: "date", nullable: true),
                    fecha_hasta = table.Column<DateOnly>(type: "date", nullable: true),
                    hora_desde = table.Column<TimeOnly>(type: "time", nullable: true),
                    hora_hasta = table.Column<TimeOnly>(type: "time", nullable: true),
                    dias_semana = table.Column<short[]>(type: "smallint[]", nullable: true),
                    cantidad_minima = table.Column<decimal>(type: "numeric(12,3)", nullable: true),
                    precio_unitario = table.Column<decimal>(type: "numeric(14,2)", nullable: true),
                    porcentaje = table.Column<decimal>(type: "numeric(5,2)", nullable: true),
                    importe_fijo = table.Column<decimal>(type: "numeric(14,2)", nullable: true),
                    prioridad = table.Column<int>(type: "integer", nullable: false, defaultValue: 0),
                    acumulable = table.Column<bool>(type: "boolean", nullable: false, defaultValue: false),
                    activo = table.Column<bool>(type: "boolean", nullable: false, defaultValue: true),
                    created_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    updated_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    deleted_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    id_tenant = table.Column<int>(type: "integer", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_ofertas", x => x.id_oferta);
                    table.UniqueConstraint("ak_ofertas_id_oferta_id_tenant", x => new { x.id_oferta, x.id_tenant });
                    table.CheckConstraint("ck_ofertas_alcance_exclusivo", "num_nonnulls(id_articulo, id_grupo, id_categoria) = 1");
                    table.CheckConstraint("ck_ofertas_beneficio_exclusivo", "num_nonnulls(precio_unitario, porcentaje, importe_fijo) = 1");
                    table.CheckConstraint("ck_ofertas_dias_semana", "dias_semana IS NULL OR dias_semana <@ ARRAY[1,2,3,4,5,6,7]::smallint[]");
                    table.CheckConstraint("ck_ofertas_ventana_valida", "(fecha_desde IS NULL OR fecha_hasta IS NULL OR fecha_hasta >= fecha_desde) AND (hora_desde IS NULL OR hora_hasta IS NULL OR hora_hasta >= hora_desde)");
                    table.ForeignKey(
                        name: "fk_ofertas_articulo",
                        columns: x => new { x.id_articulo, x.id_tenant },
                        principalTable: "articulos",
                        principalColumns: new[] { "id_articulo", "id_tenant" },
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "fk_ofertas_categoria",
                        columns: x => new { x.id_categoria, x.id_tenant },
                        principalTable: "categorias",
                        principalColumns: new[] { "id_categoria", "id_tenant" },
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "fk_ofertas_empresa",
                        columns: x => new { x.id_empresa, x.id_tenant },
                        principalTable: "empresas",
                        principalColumns: new[] { "id_empresa", "id_tenant" },
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "fk_ofertas_grupo",
                        columns: x => new { x.id_grupo, x.id_tenant },
                        principalTable: "grupos",
                        principalColumns: new[] { "id_grupo", "id_tenant" },
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "fk_ofertas_tenant",
                        column: x => x.id_tenant,
                        principalTable: "tenants",
                        principalColumn: "id_tenant",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "ofertas_listas",
                columns: table => new
                {
                    id_oferta = table.Column<int>(type: "integer", nullable: false),
                    id_lista_precio = table.Column<int>(type: "integer", nullable: false),
                    id_tenant = table.Column<int>(type: "integer", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_ofertas_listas", x => new { x.id_oferta, x.id_lista_precio });
                    table.ForeignKey(
                        name: "fk_ofertas_listas_lista_precio",
                        columns: x => new { x.id_lista_precio, x.id_tenant },
                        principalTable: "listas_precio",
                        principalColumns: new[] { "id_lista_precio", "id_tenant" },
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "fk_ofertas_listas_oferta",
                        columns: x => new { x.id_oferta, x.id_tenant },
                        principalTable: "ofertas",
                        principalColumns: new[] { "id_oferta", "id_tenant" },
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "fk_ofertas_listas_tenant",
                        column: x => x.id_tenant,
                        principalTable: "tenants",
                        principalColumn: "id_tenant",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateIndex(
                name: "ix_ofertas_articulo",
                table: "ofertas",
                columns: new[] { "id_articulo", "id_tenant" });

            migrationBuilder.CreateIndex(
                name: "ix_ofertas_categoria",
                table: "ofertas",
                columns: new[] { "id_categoria", "id_tenant" });

            migrationBuilder.CreateIndex(
                name: "ix_ofertas_empresa",
                table: "ofertas",
                columns: new[] { "id_empresa", "id_tenant" });

            migrationBuilder.CreateIndex(
                name: "ix_ofertas_grupo",
                table: "ofertas",
                columns: new[] { "id_grupo", "id_tenant" });

            migrationBuilder.CreateIndex(
                name: "ix_ofertas_tenant",
                table: "ofertas",
                column: "id_tenant");

            migrationBuilder.CreateIndex(
                name: "ix_ofertas_listas_lista_precio",
                table: "ofertas_listas",
                columns: new[] { "id_lista_precio", "id_tenant" });

            migrationBuilder.CreateIndex(
                name: "ix_ofertas_listas_oferta",
                table: "ofertas_listas",
                columns: new[] { "id_oferta", "id_tenant" });

            migrationBuilder.CreateIndex(
                name: "ix_ofertas_listas_tenant",
                table: "ofertas_listas",
                column: "id_tenant");

            // RLS (ADR-4/ADR-15, DB CHANGE GATE aprobado 2026-08-03): las 2 tablas nuevas
            // activan su policy en la misma migración que las crea.
            migrationBuilder.HabilitarRlsDeTenant("ofertas");
            migrationBuilder.HabilitarRlsDeTenant("ofertas_listas");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "ofertas_listas");

            migrationBuilder.DropTable(
                name: "ofertas");
        }
    }
}
