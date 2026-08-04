using System;
using Microsoft.EntityFrameworkCore.Migrations;
using Npgsql.EntityFrameworkCore.PostgreSQL.Metadata;
using Ways.Domain.Caja;
using Ways.Domain.Gastos;
using Ways.Infrastructure.Multitenancy;

#nullable disable

namespace Ways.Infrastructure.Persistencia.Migraciones
{
    /// <inheritdoc />
    public partial class TurnosCajaYGastosEtapa6 : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AlterDatabase()
                .Annotation("Npgsql:Enum:categoria_gasto", "impuestos,otros,proveedor,servicios,sueldos,viaticos")
                .Annotation("Npgsql:Enum:clase_comprobante", "compra,venta")
                .Annotation("Npgsql:Enum:comportamiento_medio_pago", "cuenta_corriente,efectivo,electronico")
                .Annotation("Npgsql:Enum:estado_comprobante", "anulado,emitido")
                .Annotation("Npgsql:Enum:estado_tenant", "activo,baja,suspendido")
                .Annotation("Npgsql:Enum:estado_turno", "abierto,cerrado")
                .Annotation("Npgsql:Enum:estado_usuario", "activo,bloqueado,inactivo")
                .Annotation("Npgsql:Enum:modo_lista", "derivada,fija")
                .Annotation("Npgsql:Enum:motivo_stock", "ajuste,anulacion,compra,inventario,transferencia,venta")
                .Annotation("Npgsql:Enum:tipo_documento", "cuil,cuit,dni,otro,pasaporte")
                .Annotation("Npgsql:Enum:tipo_movimiento_caja", "apertura_cajon,refuerzo,retiro")
                .Annotation("Npgsql:Enum:tipo_movimiento_cc", "actualizacion_precios,ajuste,consumo,pago")
                .Annotation("Npgsql:Enum:tipo_movimiento_tesoreria", "ajuste,deposito,gasto,retiro_caja")
                .Annotation("Npgsql:Enum:unidad_venta", "peso,unidad")
                .Annotation("Npgsql:PostgresExtension:citext", ",,")
                .OldAnnotation("Npgsql:Enum:clase_comprobante", "compra,venta")
                .OldAnnotation("Npgsql:Enum:comportamiento_medio_pago", "cuenta_corriente,efectivo,electronico")
                .OldAnnotation("Npgsql:Enum:estado_comprobante", "anulado,emitido")
                .OldAnnotation("Npgsql:Enum:estado_tenant", "activo,baja,suspendido")
                .OldAnnotation("Npgsql:Enum:estado_usuario", "activo,bloqueado,inactivo")
                .OldAnnotation("Npgsql:Enum:modo_lista", "derivada,fija")
                .OldAnnotation("Npgsql:Enum:motivo_stock", "ajuste,anulacion,compra,inventario,transferencia,venta")
                .OldAnnotation("Npgsql:Enum:tipo_documento", "cuil,cuit,dni,otro,pasaporte")
                .OldAnnotation("Npgsql:Enum:tipo_movimiento_cc", "actualizacion_precios,ajuste,consumo,pago")
                .OldAnnotation("Npgsql:Enum:unidad_venta", "peso,unidad")
                .OldAnnotation("Npgsql:PostgresExtension:citext", ",,");

            migrationBuilder.CreateTable(
                name: "turnos_caja",
                columns: table => new
                {
                    id_turno_caja = table.Column<int>(type: "integer", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    id_punto_venta = table.Column<int>(type: "integer", nullable: false),
                    id_empleado_apertura = table.Column<int>(type: "integer", nullable: false),
                    id_empleado_cierre = table.Column<int>(type: "integer", nullable: true),
                    fecha_apertura = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    fecha_cierre = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    fondo_inicial = table.Column<decimal>(type: "numeric(14,2)", nullable: false, defaultValue: 0m),
                    estado = table.Column<EstadoTurno>(type: "estado_turno", nullable: false),
                    observaciones = table.Column<string>(type: "text", nullable: true),
                    created_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    updated_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    deleted_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    id_tenant = table.Column<int>(type: "integer", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_turnos_caja", x => x.id_turno_caja);
                    table.UniqueConstraint("ak_turnos_caja_id_turno_caja_id_tenant", x => new { x.id_turno_caja, x.id_tenant });
                    table.CheckConstraint("ck_turnos_caja_cierre_consistente", "(estado = 'abierto' AND fecha_cierre IS NULL AND id_empleado_cierre IS NULL) OR (estado = 'cerrado' AND fecha_cierre IS NOT NULL AND id_empleado_cierre IS NOT NULL)");
                    table.CheckConstraint("ck_turnos_caja_fondo_inicial_no_negativo", "fondo_inicial >= 0");
                    table.ForeignKey(
                        name: "fk_turnos_caja_empleado_apertura",
                        column: x => x.id_empleado_apertura,
                        principalTable: "usuarios",
                        principalColumn: "id_usuario",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "fk_turnos_caja_empleado_cierre",
                        column: x => x.id_empleado_cierre,
                        principalTable: "usuarios",
                        principalColumn: "id_usuario",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "fk_turnos_caja_punto_venta",
                        columns: x => new { x.id_punto_venta, x.id_tenant },
                        principalTable: "puntos_venta",
                        principalColumns: new[] { "id_punto_venta", "id_tenant" },
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "fk_turnos_caja_tenant",
                        column: x => x.id_tenant,
                        principalTable: "tenants",
                        principalColumn: "id_tenant",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "arqueos_turno",
                columns: table => new
                {
                    id_arqueo = table.Column<int>(type: "integer", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    id_tenant = table.Column<int>(type: "integer", nullable: false),
                    id_turno_caja = table.Column<int>(type: "integer", nullable: false),
                    id_medio_pago = table.Column<int>(type: "integer", nullable: false),
                    importe_esperado = table.Column<decimal>(type: "numeric(14,2)", nullable: false),
                    importe_declarado = table.Column<decimal>(type: "numeric(14,2)", nullable: false),
                    diferencia = table.Column<decimal>(type: "numeric(14,2)", nullable: false, computedColumnSql: "(importe_esperado - importe_declarado)", stored: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_arqueos_turno", x => x.id_arqueo);
                    table.ForeignKey(
                        name: "fk_arqueos_turno_medio_pago",
                        columns: x => new { x.id_medio_pago, x.id_tenant },
                        principalTable: "medios_pago",
                        principalColumns: new[] { "id_medio_pago", "id_tenant" },
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "fk_arqueos_turno_tenant",
                        column: x => x.id_tenant,
                        principalTable: "tenants",
                        principalColumn: "id_tenant",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "fk_arqueos_turno_turno",
                        columns: x => new { x.id_turno_caja, x.id_tenant },
                        principalTable: "turnos_caja",
                        principalColumns: new[] { "id_turno_caja", "id_tenant" },
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "gastos",
                columns: table => new
                {
                    id_gasto = table.Column<int>(type: "integer", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    fecha = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    id_punto_venta = table.Column<int>(type: "integer", nullable: false),
                    id_turno_caja = table.Column<int>(type: "integer", nullable: false),
                    id_empleado = table.Column<int>(type: "integer", nullable: false),
                    categoria = table.Column<CategoriaGasto>(type: "categoria_gasto", nullable: false),
                    id_proveedor = table.Column<int>(type: "integer", nullable: true),
                    id_area = table.Column<int>(type: "integer", nullable: true),
                    concepto = table.Column<string>(type: "text", nullable: false),
                    detalle = table.Column<string>(type: "text", nullable: true),
                    id_medio_pago = table.Column<int>(type: "integer", nullable: false),
                    numero_factura = table.Column<string>(type: "text", nullable: true),
                    importe = table.Column<decimal>(type: "numeric(14,2)", nullable: false),
                    created_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    updated_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    deleted_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    id_tenant = table.Column<int>(type: "integer", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_gastos", x => x.id_gasto);
                    table.CheckConstraint("ck_gastos_importe_positivo", "importe > 0");
                    table.ForeignKey(
                        name: "fk_gastos_area",
                        columns: x => new { x.id_area, x.id_tenant },
                        principalTable: "areas",
                        principalColumns: new[] { "id_area", "id_tenant" },
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "fk_gastos_empleado",
                        column: x => x.id_empleado,
                        principalTable: "usuarios",
                        principalColumn: "id_usuario",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "fk_gastos_medio_pago",
                        columns: x => new { x.id_medio_pago, x.id_tenant },
                        principalTable: "medios_pago",
                        principalColumns: new[] { "id_medio_pago", "id_tenant" },
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "fk_gastos_proveedor",
                        columns: x => new { x.id_proveedor, x.id_tenant },
                        principalTable: "proveedores",
                        principalColumns: new[] { "id_proveedor", "id_tenant" },
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "fk_gastos_punto_venta",
                        columns: x => new { x.id_punto_venta, x.id_tenant },
                        principalTable: "puntos_venta",
                        principalColumns: new[] { "id_punto_venta", "id_tenant" },
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "fk_gastos_tenant",
                        column: x => x.id_tenant,
                        principalTable: "tenants",
                        principalColumn: "id_tenant",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "fk_gastos_turno",
                        columns: x => new { x.id_turno_caja, x.id_tenant },
                        principalTable: "turnos_caja",
                        principalColumns: new[] { "id_turno_caja", "id_tenant" },
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "movimientos_caja",
                columns: table => new
                {
                    id_movimiento = table.Column<int>(type: "integer", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    id_tenant = table.Column<int>(type: "integer", nullable: false),
                    id_turno_caja = table.Column<int>(type: "integer", nullable: false),
                    tipo = table.Column<TipoMovimientoCaja>(type: "tipo_movimiento_caja", nullable: false),
                    importe = table.Column<decimal>(type: "numeric(14,2)", nullable: false),
                    motivo = table.Column<string>(type: "text", nullable: false),
                    id_empleado = table.Column<int>(type: "integer", nullable: false),
                    creado_el = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_movimientos_caja", x => x.id_movimiento);
                    table.CheckConstraint("ck_movimientos_caja_importe", "(tipo = 'apertura_cajon' AND importe = 0) OR (tipo <> 'apertura_cajon' AND importe > 0)");
                    table.CheckConstraint("ck_movimientos_caja_motivo_minimo", "length(btrim(motivo)) >= 5");
                    table.ForeignKey(
                        name: "fk_movimientos_caja_empleado",
                        column: x => x.id_empleado,
                        principalTable: "usuarios",
                        principalColumn: "id_usuario",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "fk_movimientos_caja_tenant",
                        column: x => x.id_tenant,
                        principalTable: "tenants",
                        principalColumn: "id_tenant",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "fk_movimientos_caja_turno",
                        columns: x => new { x.id_turno_caja, x.id_tenant },
                        principalTable: "turnos_caja",
                        principalColumns: new[] { "id_turno_caja", "id_tenant" },
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "movimientos_tesoreria",
                columns: table => new
                {
                    id_movimiento = table.Column<int>(type: "integer", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    id_tenant = table.Column<int>(type: "integer", nullable: false),
                    id_punto_venta = table.Column<int>(type: "integer", nullable: false),
                    fecha = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    tipo = table.Column<TipoMovimientoTesoreria>(type: "tipo_movimiento_tesoreria", nullable: false),
                    id_turno_caja = table.Column<int>(type: "integer", nullable: true),
                    concepto = table.Column<string>(type: "text", nullable: false),
                    inicio = table.Column<decimal>(type: "numeric(14,2)", nullable: false),
                    ingreso = table.Column<decimal>(type: "numeric(14,2)", nullable: false),
                    egreso = table.Column<decimal>(type: "numeric(14,2)", nullable: false),
                    final = table.Column<decimal>(type: "numeric(14,2)", nullable: false),
                    id_empleado = table.Column<int>(type: "integer", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_movimientos_tesoreria", x => x.id_movimiento);
                    table.CheckConstraint("ck_movimientos_tesoreria_cadena", "final = inicio + ingreso - egreso");
                    table.ForeignKey(
                        name: "fk_movimientos_tesoreria_empleado",
                        column: x => x.id_empleado,
                        principalTable: "usuarios",
                        principalColumn: "id_usuario",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "fk_movimientos_tesoreria_punto_venta",
                        columns: x => new { x.id_punto_venta, x.id_tenant },
                        principalTable: "puntos_venta",
                        principalColumns: new[] { "id_punto_venta", "id_tenant" },
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "fk_movimientos_tesoreria_tenant",
                        column: x => x.id_tenant,
                        principalTable: "tenants",
                        principalColumn: "id_tenant",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "fk_movimientos_tesoreria_turno",
                        columns: x => new { x.id_turno_caja, x.id_tenant },
                        principalTable: "turnos_caja",
                        principalColumns: new[] { "id_turno_caja", "id_tenant" },
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateIndex(
                name: "ix_comprobantes_venta_turno",
                table: "comprobantes_venta",
                columns: new[] { "id_turno_caja", "id_tenant" });

            migrationBuilder.CreateIndex(
                name: "ix_arqueos_turno_medio_pago",
                table: "arqueos_turno",
                columns: new[] { "id_medio_pago", "id_tenant" });

            migrationBuilder.CreateIndex(
                name: "ix_arqueos_turno_tenant",
                table: "arqueos_turno",
                column: "id_tenant");

            migrationBuilder.CreateIndex(
                name: "ix_arqueos_turno_turno",
                table: "arqueos_turno",
                columns: new[] { "id_turno_caja", "id_tenant" });

            migrationBuilder.CreateIndex(
                name: "ux_arqueos_turno_medio",
                table: "arqueos_turno",
                columns: new[] { "id_turno_caja", "id_medio_pago" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "ix_gastos_area",
                table: "gastos",
                columns: new[] { "id_area", "id_tenant" });

            migrationBuilder.CreateIndex(
                name: "ix_gastos_empleado",
                table: "gastos",
                column: "id_empleado");

            migrationBuilder.CreateIndex(
                name: "ix_gastos_medio_pago",
                table: "gastos",
                columns: new[] { "id_medio_pago", "id_tenant" });

            migrationBuilder.CreateIndex(
                name: "ix_gastos_proveedor",
                table: "gastos",
                columns: new[] { "id_proveedor", "id_tenant" });

            migrationBuilder.CreateIndex(
                name: "ix_gastos_punto_venta_fecha",
                table: "gastos",
                columns: new[] { "id_punto_venta", "id_tenant", "fecha" });

            migrationBuilder.CreateIndex(
                name: "ix_gastos_tenant",
                table: "gastos",
                column: "id_tenant");

            migrationBuilder.CreateIndex(
                name: "ix_gastos_turno",
                table: "gastos",
                columns: new[] { "id_turno_caja", "id_tenant" });

            migrationBuilder.CreateIndex(
                name: "ix_movimientos_caja_empleado",
                table: "movimientos_caja",
                column: "id_empleado");

            migrationBuilder.CreateIndex(
                name: "ix_movimientos_caja_tenant",
                table: "movimientos_caja",
                column: "id_tenant");

            migrationBuilder.CreateIndex(
                name: "ix_movimientos_caja_turno",
                table: "movimientos_caja",
                columns: new[] { "id_turno_caja", "id_tenant" });

            migrationBuilder.CreateIndex(
                name: "ix_movimientos_tesoreria_empleado",
                table: "movimientos_tesoreria",
                column: "id_empleado");

            migrationBuilder.CreateIndex(
                name: "ix_movimientos_tesoreria_punto_venta_id",
                table: "movimientos_tesoreria",
                columns: new[] { "id_punto_venta", "id_tenant", "id_movimiento" });

            migrationBuilder.CreateIndex(
                name: "ix_movimientos_tesoreria_tenant",
                table: "movimientos_tesoreria",
                column: "id_tenant");

            migrationBuilder.CreateIndex(
                name: "ix_movimientos_tesoreria_turno",
                table: "movimientos_tesoreria",
                columns: new[] { "id_turno_caja", "id_tenant" });

            migrationBuilder.CreateIndex(
                name: "ix_turnos_caja_empleado_apertura",
                table: "turnos_caja",
                column: "id_empleado_apertura");

            migrationBuilder.CreateIndex(
                name: "ix_turnos_caja_empleado_cierre",
                table: "turnos_caja",
                column: "id_empleado_cierre");

            migrationBuilder.CreateIndex(
                name: "ix_turnos_caja_punto_venta_fecha",
                table: "turnos_caja",
                columns: new[] { "id_punto_venta", "id_tenant", "fecha_apertura" });

            migrationBuilder.CreateIndex(
                name: "ix_turnos_caja_tenant",
                table: "turnos_caja",
                column: "id_tenant");

            migrationBuilder.CreateIndex(
                name: "ux_turnos_caja_abierto",
                table: "turnos_caja",
                column: "id_punto_venta",
                unique: true,
                filter: "estado = 'abierto'");

            migrationBuilder.AddForeignKey(
                name: "fk_comprobantes_venta_turno",
                table: "comprobantes_venta",
                columns: new[] { "id_turno_caja", "id_tenant" },
                principalTable: "turnos_caja",
                principalColumns: new[] { "id_turno_caja", "id_tenant" },
                onDelete: ReferentialAction.Restrict);

            // RLS (ADR-4/ADR-15, DB CHANGE GATE aprobado 2026-08-04): cada tabla nueva activa
            // su policy en la misma migración que la crea (design: Migration/Rollout).
            migrationBuilder.HabilitarRlsDeTenant("turnos_caja");
            migrationBuilder.HabilitarRlsDeTenant("movimientos_caja");
            migrationBuilder.HabilitarRlsDeTenant("arqueos_turno");
            migrationBuilder.HabilitarRlsDeTenant("movimientos_tesoreria");
            migrationBuilder.HabilitarRlsDeTenant("gastos");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropForeignKey(
                name: "fk_comprobantes_venta_turno",
                table: "comprobantes_venta");

            migrationBuilder.DropTable(
                name: "arqueos_turno");

            migrationBuilder.DropTable(
                name: "gastos");

            migrationBuilder.DropTable(
                name: "movimientos_caja");

            migrationBuilder.DropTable(
                name: "movimientos_tesoreria");

            migrationBuilder.DropTable(
                name: "turnos_caja");

            migrationBuilder.DropIndex(
                name: "ix_comprobantes_venta_turno",
                table: "comprobantes_venta");

            migrationBuilder.AlterDatabase()
                .Annotation("Npgsql:Enum:clase_comprobante", "compra,venta")
                .Annotation("Npgsql:Enum:comportamiento_medio_pago", "cuenta_corriente,efectivo,electronico")
                .Annotation("Npgsql:Enum:estado_comprobante", "anulado,emitido")
                .Annotation("Npgsql:Enum:estado_tenant", "activo,baja,suspendido")
                .Annotation("Npgsql:Enum:estado_usuario", "activo,bloqueado,inactivo")
                .Annotation("Npgsql:Enum:modo_lista", "derivada,fija")
                .Annotation("Npgsql:Enum:motivo_stock", "ajuste,anulacion,compra,inventario,transferencia,venta")
                .Annotation("Npgsql:Enum:tipo_documento", "cuil,cuit,dni,otro,pasaporte")
                .Annotation("Npgsql:Enum:tipo_movimiento_cc", "actualizacion_precios,ajuste,consumo,pago")
                .Annotation("Npgsql:Enum:unidad_venta", "peso,unidad")
                .Annotation("Npgsql:PostgresExtension:citext", ",,")
                .OldAnnotation("Npgsql:Enum:categoria_gasto", "impuestos,otros,proveedor,servicios,sueldos,viaticos")
                .OldAnnotation("Npgsql:Enum:clase_comprobante", "compra,venta")
                .OldAnnotation("Npgsql:Enum:comportamiento_medio_pago", "cuenta_corriente,efectivo,electronico")
                .OldAnnotation("Npgsql:Enum:estado_comprobante", "anulado,emitido")
                .OldAnnotation("Npgsql:Enum:estado_tenant", "activo,baja,suspendido")
                .OldAnnotation("Npgsql:Enum:estado_turno", "abierto,cerrado")
                .OldAnnotation("Npgsql:Enum:estado_usuario", "activo,bloqueado,inactivo")
                .OldAnnotation("Npgsql:Enum:modo_lista", "derivada,fija")
                .OldAnnotation("Npgsql:Enum:motivo_stock", "ajuste,anulacion,compra,inventario,transferencia,venta")
                .OldAnnotation("Npgsql:Enum:tipo_documento", "cuil,cuit,dni,otro,pasaporte")
                .OldAnnotation("Npgsql:Enum:tipo_movimiento_caja", "apertura_cajon,refuerzo,retiro")
                .OldAnnotation("Npgsql:Enum:tipo_movimiento_cc", "actualizacion_precios,ajuste,consumo,pago")
                .OldAnnotation("Npgsql:Enum:tipo_movimiento_tesoreria", "ajuste,deposito,gasto,retiro_caja")
                .OldAnnotation("Npgsql:Enum:unidad_venta", "peso,unidad")
                .OldAnnotation("Npgsql:PostgresExtension:citext", ",,");
        }
    }
}
