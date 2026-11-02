using System;
using Microsoft.EntityFrameworkCore.Migrations;
using Npgsql.EntityFrameworkCore.PostgreSQL.Metadata;
using Ways.Infrastructure.Multitenancy;

#nullable disable

namespace Ways.Infrastructure.Persistencia.Migraciones
{
    /// <inheritdoc />
    public partial class ConciliacionMercadoPago : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<decimal>(
                name: "comision_real",
                table: "cobros_mercadopago",
                type: "numeric(18,2)",
                nullable: true);

            migrationBuilder.AddColumn<DateTimeOffset>(
                name: "fecha_liberacion",
                table: "cobros_mercadopago",
                type: "timestamp with time zone",
                nullable: true);

            migrationBuilder.AddColumn<int>(
                name: "id_fila_conciliacion",
                table: "cobros_mercadopago",
                type: "integer",
                nullable: true);

            migrationBuilder.AddColumn<decimal>(
                name: "neto_real",
                table: "cobros_mercadopago",
                type: "numeric(18,2)",
                nullable: true);

            migrationBuilder.CreateTable(
                name: "conciliaciones_mercadopago",
                columns: table => new
                {
                    id_conciliacion_mercadopago = table.Column<int>(type: "integer", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    id_empresa = table.Column<int>(type: "integer", nullable: false),
                    hash_archivo = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    nombre_archivo = table.Column<string>(type: "character varying(255)", maxLength: 255, nullable: true),
                    fecha_desde = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    fecha_hasta = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    id_usuario = table.Column<int>(type: "integer", nullable: false),
                    cantidad_filas = table.Column<int>(type: "integer", nullable: false),
                    cantidad_invalidas = table.Column<int>(type: "integer", nullable: false),
                    created_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    updated_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    deleted_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    id_tenant = table.Column<int>(type: "integer", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_conciliaciones_mercadopago", x => x.id_conciliacion_mercadopago);
                    table.UniqueConstraint("ak_conciliaciones_mercadopago_id_id_tenant", x => new { x.id_conciliacion_mercadopago, x.id_tenant });
                    table.CheckConstraint("ck_conciliaciones_mercadopago_cantidades", "cantidad_filas >= 0 AND cantidad_invalidas >= 0 AND cantidad_invalidas <= cantidad_filas");
                    table.CheckConstraint("ck_conciliaciones_mercadopago_hash", "length(hash_archivo) = 64");
                    table.CheckConstraint("ck_conciliaciones_mercadopago_periodo", "fecha_desde IS NULL OR fecha_hasta IS NULL OR fecha_desde <= fecha_hasta");
                    table.ForeignKey(
                        name: "fk_conciliaciones_mercadopago_empresa",
                        columns: x => new { x.id_empresa, x.id_tenant },
                        principalTable: "empresas",
                        principalColumns: new[] { "id_empresa", "id_tenant" },
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "fk_conciliaciones_mercadopago_tenant",
                        column: x => x.id_tenant,
                        principalTable: "tenants",
                        principalColumn: "id_tenant",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "fk_conciliaciones_mercadopago_usuario",
                        column: x => x.id_usuario,
                        principalTable: "usuarios",
                        principalColumn: "id_usuario",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "filas_conciliacion_mercadopago",
                columns: table => new
                {
                    id_fila_conciliacion_mercadopago = table.Column<int>(type: "integer", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    id_empresa = table.Column<int>(type: "integer", nullable: false),
                    id_conciliacion_mercadopago = table.Column<int>(type: "integer", nullable: false),
                    numero_linea = table.Column<int>(type: "integer", nullable: false),
                    resultado = table.Column<string>(type: "character varying(20)", maxLength: 20, nullable: false),
                    source_id = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: true),
                    external_reference = table.Column<string>(type: "character varying(128)", maxLength: 128, nullable: true),
                    tipo_transaccion = table.Column<string>(type: "character varying(20)", maxLength: 20, nullable: true),
                    importe = table.Column<decimal>(type: "numeric(18,2)", nullable: true),
                    comision = table.Column<decimal>(type: "numeric(18,2)", nullable: true),
                    neto = table.Column<decimal>(type: "numeric(18,2)", nullable: true),
                    fecha_transaccion = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    fecha_liberacion = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    external_pos_id = table.Column<string>(type: "character varying(80)", maxLength: 80, nullable: true),
                    motivo = table.Column<string>(type: "character varying(300)", maxLength: 300, nullable: true),
                    id_cobro_mercadopago = table.Column<int>(type: "integer", nullable: true),
                    id_cobro = table.Column<int>(type: "integer", nullable: true),
                    created_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    updated_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    deleted_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    id_tenant = table.Column<int>(type: "integer", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_filas_conciliacion_mercadopago", x => x.id_fila_conciliacion_mercadopago);
                    table.UniqueConstraint("ak_filas_conciliacion_mercadopago_id_id_tenant", x => new { x.id_fila_conciliacion_mercadopago, x.id_tenant });
                    table.CheckConstraint("ck_filas_conciliacion_mercadopago_confirmada", "id_cobro IS NULL OR resultado = 'sin_cobro_en_ways'");
                    table.CheckConstraint("ck_filas_conciliacion_mercadopago_datos", "(resultado = 'invalida' AND motivo IS NOT NULL AND source_id IS NULL AND importe IS NULL AND fecha_transaccion IS NULL AND id_cobro_mercadopago IS NULL AND id_cobro IS NULL) OR (resultado <> 'invalida' AND motivo IS NULL AND source_id IS NOT NULL AND tipo_transaccion IS NOT NULL AND importe IS NOT NULL AND fecha_transaccion IS NOT NULL)");
                    table.CheckConstraint("ck_filas_conciliacion_mercadopago_linea", "numero_linea >= 2");
                    table.CheckConstraint("ck_filas_conciliacion_mercadopago_resultado", "resultado IN ('conciliado', 'sin_venta', 'importe_distinto', 'sin_cobro_en_ways', 'devolucion', 'contracargo', 'retiro', 'invalida')");
                    table.ForeignKey(
                        name: "fk_filas_conciliacion_mercadopago_cobro",
                        columns: x => new { x.id_cobro, x.id_tenant },
                        principalTable: "cobros",
                        principalColumns: new[] { "id_cobro", "id_tenant" },
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "fk_filas_conciliacion_mercadopago_cobro_mp",
                        columns: x => new { x.id_cobro_mercadopago, x.id_tenant },
                        principalTable: "cobros_mercadopago",
                        principalColumns: new[] { "id_cobro_mercadopago", "id_tenant" },
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "fk_filas_conciliacion_mercadopago_conciliacion",
                        columns: x => new { x.id_conciliacion_mercadopago, x.id_tenant },
                        principalTable: "conciliaciones_mercadopago",
                        principalColumns: new[] { "id_conciliacion_mercadopago", "id_tenant" },
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "fk_filas_conciliacion_mercadopago_empresa",
                        columns: x => new { x.id_empresa, x.id_tenant },
                        principalTable: "empresas",
                        principalColumns: new[] { "id_empresa", "id_tenant" },
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "fk_filas_conciliacion_mercadopago_tenant",
                        column: x => x.id_tenant,
                        principalTable: "tenants",
                        principalColumn: "id_tenant",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateIndex(
                name: "ix_cobros_mercadopago_fila_conciliacion",
                table: "cobros_mercadopago",
                columns: new[] { "id_fila_conciliacion", "id_tenant" });

            migrationBuilder.AddCheckConstraint(
                name: "ck_cobros_mercadopago_conciliacion",
                table: "cobros_mercadopago",
                sql: "id_fila_conciliacion IS NOT NULL OR (comision_real IS NULL AND neto_real IS NULL AND fecha_liberacion IS NULL)");

            migrationBuilder.CreateIndex(
                name: "IX_conciliaciones_mercadopago_id_empresa_id_tenant",
                table: "conciliaciones_mercadopago",
                columns: new[] { "id_empresa", "id_tenant" });

            migrationBuilder.CreateIndex(
                name: "ix_conciliaciones_mercadopago_tenant",
                table: "conciliaciones_mercadopago",
                column: "id_tenant");

            migrationBuilder.CreateIndex(
                name: "ix_conciliaciones_mercadopago_usuario",
                table: "conciliaciones_mercadopago",
                column: "id_usuario");

            migrationBuilder.CreateIndex(
                name: "ux_conciliaciones_mercadopago_archivo",
                table: "conciliaciones_mercadopago",
                columns: new[] { "id_empresa", "hash_archivo" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "ix_filas_conciliacion_mercadopago_cobro_mp",
                table: "filas_conciliacion_mercadopago",
                columns: new[] { "id_cobro_mercadopago", "id_tenant" });

            migrationBuilder.CreateIndex(
                name: "ix_filas_conciliacion_mercadopago_empresa",
                table: "filas_conciliacion_mercadopago",
                columns: new[] { "id_empresa", "id_tenant" });

            migrationBuilder.CreateIndex(
                name: "IX_filas_conciliacion_mercadopago_id_conciliacion_mercadopago_~",
                table: "filas_conciliacion_mercadopago",
                columns: new[] { "id_conciliacion_mercadopago", "id_tenant" });

            migrationBuilder.CreateIndex(
                name: "ix_filas_conciliacion_mercadopago_resultado",
                table: "filas_conciliacion_mercadopago",
                columns: new[] { "id_conciliacion_mercadopago", "resultado" });

            migrationBuilder.CreateIndex(
                name: "ix_filas_conciliacion_mercadopago_tenant",
                table: "filas_conciliacion_mercadopago",
                column: "id_tenant");

            migrationBuilder.CreateIndex(
                name: "ux_filas_conciliacion_mercadopago_cobro",
                table: "filas_conciliacion_mercadopago",
                columns: new[] { "id_cobro", "id_tenant" },
                unique: true,
                filter: "id_cobro IS NOT NULL");

            migrationBuilder.CreateIndex(
                name: "ux_filas_conciliacion_mercadopago_linea",
                table: "filas_conciliacion_mercadopago",
                columns: new[] { "id_conciliacion_mercadopago", "numero_linea" },
                unique: true);

            migrationBuilder.AddForeignKey(
                name: "fk_cobros_mercadopago_fila_conciliacion",
                table: "cobros_mercadopago",
                columns: new[] { "id_fila_conciliacion", "id_tenant" },
                principalTable: "filas_conciliacion_mercadopago",
                principalColumns: new[] { "id_fila_conciliacion_mercadopago", "id_tenant" },
                onDelete: ReferentialAction.Restrict);

            migrationBuilder.HabilitarRlsDeTenant("conciliaciones_mercadopago");
            migrationBuilder.HabilitarRlsDeTenant("filas_conciliacion_mercadopago");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropForeignKey(
                name: "fk_cobros_mercadopago_fila_conciliacion",
                table: "cobros_mercadopago");

            migrationBuilder.DropTable(
                name: "filas_conciliacion_mercadopago");

            migrationBuilder.DropTable(
                name: "conciliaciones_mercadopago");

            migrationBuilder.DropIndex(
                name: "ix_cobros_mercadopago_fila_conciliacion",
                table: "cobros_mercadopago");

            migrationBuilder.DropCheckConstraint(
                name: "ck_cobros_mercadopago_conciliacion",
                table: "cobros_mercadopago");

            migrationBuilder.DropColumn(
                name: "comision_real",
                table: "cobros_mercadopago");

            migrationBuilder.DropColumn(
                name: "fecha_liberacion",
                table: "cobros_mercadopago");

            migrationBuilder.DropColumn(
                name: "id_fila_conciliacion",
                table: "cobros_mercadopago");

            migrationBuilder.DropColumn(
                name: "neto_real",
                table: "cobros_mercadopago");
        }
    }
}
