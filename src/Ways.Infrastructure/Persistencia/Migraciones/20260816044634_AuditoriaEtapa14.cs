using System;
using Microsoft.EntityFrameworkCore.Migrations;
using Npgsql.EntityFrameworkCore.PostgreSQL.Metadata;
using Ways.Infrastructure.Multitenancy;

#nullable disable

namespace Ways.Infrastructure.Persistencia.Migraciones
{
    /// <inheritdoc />
    public partial class AuditoriaEtapa14 : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "auditoria",
                columns: table => new
                {
                    id_auditoria = table.Column<long>(type: "bigint", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    id_tenant = table.Column<int>(type: "integer", nullable: false),
                    id_punto_venta = table.Column<int>(type: "integer", nullable: true),
                    id_actor = table.Column<int>(type: "integer", nullable: false),
                    accion = table.Column<string>(type: "text", nullable: false),
                    entidad = table.Column<string>(type: "text", nullable: false),
                    id_entidad = table.Column<int>(type: "integer", nullable: false),
                    valor_anterior = table.Column<string>(type: "jsonb", nullable: true),
                    valor_nuevo = table.Column<string>(type: "jsonb", nullable: false),
                    creado_el = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_auditoria", x => x.id_auditoria);
                    table.CheckConstraint("ck_auditoria_accion_no_vacia", "length(btrim(accion)) > 0");
                    table.CheckConstraint("ck_auditoria_entidad_no_vacia", "length(btrim(entidad)) > 0");
                    table.ForeignKey(
                        name: "fk_auditoria_actor",
                        column: x => x.id_actor,
                        principalTable: "usuarios",
                        principalColumn: "id_usuario",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "fk_auditoria_punto_venta",
                        columns: x => new { x.id_punto_venta, x.id_tenant },
                        principalTable: "puntos_venta",
                        principalColumns: new[] { "id_punto_venta", "id_tenant" },
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "fk_auditoria_tenant",
                        column: x => x.id_tenant,
                        principalTable: "tenants",
                        principalColumn: "id_tenant",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateIndex(
                name: "ix_auditoria_actor",
                table: "auditoria",
                columns: new[] { "id_tenant", "id_actor", "creado_el" },
                descending: new[] { false, false, true });

            migrationBuilder.CreateIndex(
                name: "ix_auditoria_entidad",
                table: "auditoria",
                columns: new[] { "id_tenant", "entidad", "id_entidad" });

            migrationBuilder.CreateIndex(
                name: "ix_auditoria_id_actor",
                table: "auditoria",
                column: "id_actor");

            migrationBuilder.CreateIndex(
                name: "ix_auditoria_punto_venta",
                table: "auditoria",
                columns: new[] { "id_punto_venta", "id_tenant" });

            migrationBuilder.CreateIndex(
                name: "ix_auditoria_tenant_creado",
                table: "auditoria",
                columns: new[] { "id_tenant", "creado_el" },
                descending: new[] { false, true });

            // RLS al final (ADR-15): la tabla ya existe acá, misma migración que la crea — nunca
            // una ventana con la tabla scopeada y sin policy. Policy estándar, sin desvío
            // (proposal §A): USING/WITH CHECK (app_es_plataforma() OR id_tenant = app_tenant_actual()).
            migrationBuilder.HabilitarRlsDeTenant("auditoria");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "auditoria");
        }
    }
}
