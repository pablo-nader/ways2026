using System;
using Microsoft.EntityFrameworkCore.Migrations;
using Npgsql.EntityFrameworkCore.PostgreSQL.Metadata;
using Ways.Infrastructure.Multitenancy;

#nullable disable

namespace Ways.Infrastructure.Persistencia.Migraciones
{
    /// <inheritdoc />
    public partial class ReservaDeNumeracion : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddUniqueConstraint(
                name: "ak_dispositivos_id_dispositivo_id_tenant",
                table: "dispositivos",
                columns: new[] { "id_dispositivo", "id_tenant" });

            migrationBuilder.CreateTable(
                name: "reservas_numeracion",
                columns: table => new
                {
                    id_reserva_numeracion = table.Column<long>(type: "bigint", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    id_tenant = table.Column<int>(type: "integer", nullable: false),
                    id_punto_venta = table.Column<int>(type: "integer", nullable: false),
                    tipo_comprobante = table.Column<string>(type: "character varying(30)", maxLength: 30, nullable: false),
                    id_dispositivo = table.Column<int>(type: "integer", nullable: false),
                    desde = table.Column<long>(type: "bigint", nullable: false),
                    hasta = table.Column<long>(type: "bigint", nullable: false),
                    abandonada_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    created_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    updated_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_reservas_numeracion", x => x.id_reserva_numeracion);
                    table.CheckConstraint("ck_reservas_numeracion_rango", "hasta >= desde");
                    table.ForeignKey(
                        name: "fk_reservas_numeracion_dispositivo",
                        columns: x => new { x.id_dispositivo, x.id_tenant },
                        principalTable: "dispositivos",
                        principalColumns: new[] { "id_dispositivo", "id_tenant" },
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "fk_reservas_numeracion_punto_venta",
                        columns: x => new { x.id_punto_venta, x.id_tenant },
                        principalTable: "puntos_venta",
                        principalColumns: new[] { "id_punto_venta", "id_tenant" },
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "fk_reservas_numeracion_tenant",
                        column: x => x.id_tenant,
                        principalTable: "tenants",
                        principalColumn: "id_tenant",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateIndex(
                name: "ix_reservas_numeracion_dispositivo",
                table: "reservas_numeracion",
                columns: new[] { "id_dispositivo", "id_tenant" });

            migrationBuilder.CreateIndex(
                name: "ix_reservas_numeracion_punto_venta",
                table: "reservas_numeracion",
                columns: new[] { "id_punto_venta", "id_tenant" });

            migrationBuilder.CreateIndex(
                name: "ix_reservas_numeracion_tenant",
                table: "reservas_numeracion",
                column: "id_tenant");

            migrationBuilder.CreateIndex(
                name: "ux_reservas_numeracion_dispositivo_activo",
                table: "reservas_numeracion",
                columns: new[] { "id_tenant", "id_punto_venta", "tipo_comprobante", "id_dispositivo" },
                unique: true,
                filter: "abandonada_at IS NULL");

            // --- Aislamiento por RLS sobre reservas_numeracion (doc 09, ADR-4/ADR-5/ADR-15) ---
            // Tabla scopeada estándar (sin excepción de modo login, a diferencia de
            // dispositivos/usuarios): el único llamador es un dispositivo YA autenticado en modo
            // Tenant.
            migrationBuilder.HabilitarRlsDeTenant("reservas_numeracion");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql("DROP POLICY IF EXISTS reservas_numeracion_tenant ON reservas_numeracion;");
            migrationBuilder.Sql("ALTER TABLE reservas_numeracion NO FORCE ROW LEVEL SECURITY;");
            migrationBuilder.Sql("ALTER TABLE reservas_numeracion DISABLE ROW LEVEL SECURITY;");

            migrationBuilder.DropTable(
                name: "reservas_numeracion");

            migrationBuilder.DropUniqueConstraint(
                name: "ak_dispositivos_id_dispositivo_id_tenant",
                table: "dispositivos");
        }
    }
}
