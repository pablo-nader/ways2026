using System;
using Microsoft.EntityFrameworkCore.Migrations;
using Npgsql.EntityFrameworkCore.PostgreSQL.Metadata;
using Ways.Infrastructure.Multitenancy;

#nullable disable

namespace Ways.Infrastructure.Persistencia.Migraciones
{
    /// <inheritdoc />
    public partial class PropuestasDeTesoreriaMercadoPago : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddUniqueConstraint(
                name: "ak_movimientos_cuenta_id_id_tenant",
                table: "movimientos_cuenta",
                columns: new[] { "id_movimiento_cuenta", "id_tenant" });

            migrationBuilder.CreateTable(
                name: "propuestas_tesoreria_mercadopago",
                columns: table => new
                {
                    id_propuesta_tesoreria_mercadopago = table.Column<int>(type: "integer", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    id_empresa = table.Column<int>(type: "integer", nullable: false),
                    id_conciliacion_mercadopago = table.Column<int>(type: "integer", nullable: false),
                    tipo = table.Column<string>(type: "character varying(20)", maxLength: 20, nullable: false),
                    clave_origen = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    importe = table.Column<decimal>(type: "numeric(14,2)", nullable: false),
                    id_fila_conciliacion_mercadopago = table.Column<int>(type: "integer", nullable: true),
                    id_cuenta_origen = table.Column<int>(type: "integer", nullable: true),
                    id_cuenta_destino = table.Column<int>(type: "integer", nullable: true),
                    estado = table.Column<string>(type: "character varying(12)", maxLength: 12, nullable: false),
                    detalle = table.Column<string>(type: "jsonb", nullable: true),
                    id_movimiento_cuenta = table.Column<int>(type: "integer", nullable: true),
                    id_movimiento_cuenta_destino = table.Column<int>(type: "integer", nullable: true),
                    id_usuario_resolucion = table.Column<int>(type: "integer", nullable: true),
                    fecha_resolucion = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    motivo_resolucion = table.Column<string>(type: "character varying(120)", maxLength: 120, nullable: true),
                    created_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    updated_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    deleted_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    id_tenant = table.Column<int>(type: "integer", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_propuestas_tesoreria_mercadopago", x => x.id_propuesta_tesoreria_mercadopago);
                    table.CheckConstraint("ck_propuestas_tesoreria_mercadopago_aplicada", "(estado = 'aplicada') = (id_movimiento_cuenta IS NOT NULL) AND (estado = 'aplicada' AND tipo <> 'diferencia_comision') = (id_movimiento_cuenta_destino IS NOT NULL) AND (tipo = 'retiro_cancelado' OR (estado = 'aplicada' AND tipo = 'retiro') = (id_cuenta_destino IS NOT NULL)) AND (estado <> 'aplicada' OR id_cuenta_origen IS NOT NULL)");
                    table.CheckConstraint("ck_propuestas_tesoreria_mercadopago_cuentas", "id_cuenta_origen IS NULL OR id_cuenta_destino IS NULL OR id_cuenta_origen <> id_cuenta_destino");
                    table.CheckConstraint("ck_propuestas_tesoreria_mercadopago_estado", "estado IN ('pendiente', 'aplicada', 'descartada')");
                    table.CheckConstraint("ck_propuestas_tesoreria_mercadopago_forma", "(tipo = 'diferencia_comision' AND id_fila_conciliacion_mercadopago IS NULL AND id_cuenta_origen IS NOT NULL AND id_cuenta_destino IS NULL AND detalle IS NOT NULL AND id_movimiento_cuenta_destino IS NULL) OR (tipo = 'retiro' AND id_fila_conciliacion_mercadopago IS NOT NULL AND importe > 0 AND detalle IS NULL) OR (tipo = 'retiro_cancelado' AND id_fila_conciliacion_mercadopago IS NOT NULL AND importe > 0 AND detalle IS NULL AND id_cuenta_origen IS NOT NULL AND id_cuenta_destino IS NOT NULL)");
                    table.CheckConstraint("ck_propuestas_tesoreria_mercadopago_importe", "importe <> 0");
                    table.CheckConstraint("ck_propuestas_tesoreria_mercadopago_motivo", "motivo_resolucion IS NULL OR estado = 'descartada'");
                    table.CheckConstraint("ck_propuestas_tesoreria_mercadopago_resolucion", "(estado = 'pendiente') = (id_usuario_resolucion IS NULL AND fecha_resolucion IS NULL)");
                    table.CheckConstraint("ck_propuestas_tesoreria_mercadopago_tipo", "tipo IN ('diferencia_comision', 'retiro', 'retiro_cancelado')");
                    table.ForeignKey(
                        name: "fk_propuestas_tesoreria_mercadopago_conciliacion",
                        columns: x => new { x.id_conciliacion_mercadopago, x.id_tenant },
                        principalTable: "conciliaciones_mercadopago",
                        principalColumns: new[] { "id_conciliacion_mercadopago", "id_tenant" },
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "fk_propuestas_tesoreria_mercadopago_cuenta_destino",
                        columns: x => new { x.id_cuenta_destino, x.id_empresa, x.id_tenant },
                        principalTable: "cuentas_tesoreria",
                        principalColumns: new[] { "id_cuenta_tesoreria", "id_empresa", "id_tenant" },
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "fk_propuestas_tesoreria_mercadopago_cuenta_origen",
                        columns: x => new { x.id_cuenta_origen, x.id_empresa, x.id_tenant },
                        principalTable: "cuentas_tesoreria",
                        principalColumns: new[] { "id_cuenta_tesoreria", "id_empresa", "id_tenant" },
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "fk_propuestas_tesoreria_mercadopago_empresa",
                        columns: x => new { x.id_empresa, x.id_tenant },
                        principalTable: "empresas",
                        principalColumns: new[] { "id_empresa", "id_tenant" },
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "fk_propuestas_tesoreria_mercadopago_fila",
                        columns: x => new { x.id_fila_conciliacion_mercadopago, x.id_tenant },
                        principalTable: "filas_conciliacion_mercadopago",
                        principalColumns: new[] { "id_fila_conciliacion_mercadopago", "id_tenant" },
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "fk_propuestas_tesoreria_mercadopago_movimiento",
                        columns: x => new { x.id_movimiento_cuenta, x.id_tenant },
                        principalTable: "movimientos_cuenta",
                        principalColumns: new[] { "id_movimiento_cuenta", "id_tenant" },
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "fk_propuestas_tesoreria_mercadopago_movimiento_destino",
                        columns: x => new { x.id_movimiento_cuenta_destino, x.id_tenant },
                        principalTable: "movimientos_cuenta",
                        principalColumns: new[] { "id_movimiento_cuenta", "id_tenant" },
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "fk_propuestas_tesoreria_mercadopago_tenant",
                        column: x => x.id_tenant,
                        principalTable: "tenants",
                        principalColumn: "id_tenant",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "fk_propuestas_tesoreria_mercadopago_usuario",
                        column: x => x.id_usuario_resolucion,
                        principalTable: "usuarios",
                        principalColumn: "id_usuario",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateIndex(
                name: "ix_propuestas_tesoreria_mercadopago_conciliacion",
                table: "propuestas_tesoreria_mercadopago",
                columns: new[] { "id_conciliacion_mercadopago", "id_tenant" });

            migrationBuilder.CreateIndex(
                name: "ix_propuestas_tesoreria_mercadopago_cuenta_destino",
                table: "propuestas_tesoreria_mercadopago",
                columns: new[] { "id_cuenta_destino", "id_empresa", "id_tenant" });

            migrationBuilder.CreateIndex(
                name: "ix_propuestas_tesoreria_mercadopago_cuenta_origen",
                table: "propuestas_tesoreria_mercadopago",
                columns: new[] { "id_cuenta_origen", "id_empresa", "id_tenant" });

            migrationBuilder.CreateIndex(
                name: "ix_propuestas_tesoreria_mercadopago_empresa",
                table: "propuestas_tesoreria_mercadopago",
                columns: new[] { "id_empresa", "id_tenant" });

            migrationBuilder.CreateIndex(
                name: "ix_propuestas_tesoreria_mercadopago_fila",
                table: "propuestas_tesoreria_mercadopago",
                columns: new[] { "id_fila_conciliacion_mercadopago", "id_tenant" });

            migrationBuilder.CreateIndex(
                name: "ix_propuestas_tesoreria_mercadopago_movimiento",
                table: "propuestas_tesoreria_mercadopago",
                columns: new[] { "id_movimiento_cuenta", "id_tenant" });

            migrationBuilder.CreateIndex(
                name: "ix_propuestas_tesoreria_mercadopago_movimiento_destino",
                table: "propuestas_tesoreria_mercadopago",
                columns: new[] { "id_movimiento_cuenta_destino", "id_tenant" });

            migrationBuilder.CreateIndex(
                name: "ix_propuestas_tesoreria_mercadopago_tenant",
                table: "propuestas_tesoreria_mercadopago",
                column: "id_tenant");

            migrationBuilder.CreateIndex(
                name: "ix_propuestas_tesoreria_mercadopago_usuario",
                table: "propuestas_tesoreria_mercadopago",
                column: "id_usuario_resolucion");

            migrationBuilder.CreateIndex(
                name: "ux_propuestas_tesoreria_mercadopago_origen",
                table: "propuestas_tesoreria_mercadopago",
                columns: new[] { "id_conciliacion_mercadopago", "tipo", "clave_origen" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "ux_propuestas_tesoreria_mercadopago_retiro",
                table: "propuestas_tesoreria_mercadopago",
                columns: new[] { "id_tenant", "id_empresa", "clave_origen" },
                unique: true,
                filter: "tipo = 'retiro'");

            migrationBuilder.CreateIndex(
                name: "ux_propuestas_tesoreria_mercadopago_retiro_cancelado",
                table: "propuestas_tesoreria_mercadopago",
                columns: new[] { "id_tenant", "id_empresa", "clave_origen" },
                unique: true,
                filter: "tipo = 'retiro_cancelado'");

            migrationBuilder.HabilitarRlsDeTenant("propuestas_tesoreria_mercadopago");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "propuestas_tesoreria_mercadopago");

            migrationBuilder.DropUniqueConstraint(
                name: "ak_movimientos_cuenta_id_id_tenant",
                table: "movimientos_cuenta");
        }
    }
}
