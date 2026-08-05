using System;
using Microsoft.EntityFrameworkCore.Migrations;
using Npgsql.EntityFrameworkCore.PostgreSQL.Metadata;
using Ways.Domain.Compras;
using Ways.Infrastructure.Multitenancy;

#nullable disable

namespace Ways.Infrastructure.Persistencia.Migraciones
{
    /// <inheritdoc />
    public partial class ComprasYTransferenciasEtapa8 : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AlterDatabase()
                .Annotation("Npgsql:Enum:categoria_gasto", "impuestos,otros,proveedor,servicios,sueldos,viaticos")
                .Annotation("Npgsql:Enum:clase_comprobante", "compra,venta")
                .Annotation("Npgsql:Enum:comportamiento_medio_pago", "cuenta_corriente,efectivo,electronico")
                .Annotation("Npgsql:Enum:estado_compra", "anulada,borrador,confirmada")
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

            migrationBuilder.AddColumn<int>(
                name: "id_comprobante_compra",
                table: "movimientos_stock",
                type: "integer",
                nullable: true);

            migrationBuilder.AddColumn<int>(
                name: "id_comprobante_compra",
                table: "gastos",
                type: "integer",
                nullable: true);

            migrationBuilder.CreateTable(
                name: "comprobantes_compra",
                columns: table => new
                {
                    id_comprobante_compra = table.Column<int>(type: "integer", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    id_proveedor = table.Column<int>(type: "integer", nullable: false),
                    id_tipo_comprobante = table.Column<int>(type: "integer", nullable: false),
                    numero_externo = table.Column<string>(type: "citext", nullable: true),
                    fecha_comprobante = table.Column<DateOnly>(type: "date", nullable: true),
                    fecha_recepcion = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    id_punto_venta = table.Column<int>(type: "integer", nullable: false),
                    id_empleado = table.Column<int>(type: "integer", nullable: false),
                    subtotal = table.Column<decimal>(type: "numeric(14,2)", nullable: false),
                    descuento_total = table.Column<decimal>(type: "numeric(14,2)", nullable: false),
                    total = table.Column<decimal>(type: "numeric(14,2)", nullable: false),
                    iva_total = table.Column<decimal>(type: "numeric(14,2)", nullable: true),
                    observaciones = table.Column<string>(type: "text", nullable: true),
                    estado = table.Column<EstadoCompra>(type: "estado_compra", nullable: false),
                    created_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    updated_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    deleted_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    id_tenant = table.Column<int>(type: "integer", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_comprobantes_compra", x => x.id_comprobante_compra);
                    table.UniqueConstraint("ak_comprobantes_compra_id_comprobante_compra_id_tenant", x => new { x.id_comprobante_compra, x.id_tenant });
                    table.CheckConstraint("ck_comprobantes_compra_confirmada_completa", "estado = 'borrador' OR (numero_externo IS NOT NULL AND fecha_comprobante IS NOT NULL AND fecha_recepcion IS NOT NULL)");
                    table.CheckConstraint("ck_comprobantes_compra_totales_no_negativos", "subtotal >= 0 AND descuento_total >= 0 AND total >= 0 AND (iva_total IS NULL OR iva_total >= 0)");
                    table.ForeignKey(
                        name: "fk_comprobantes_compra_empleado",
                        column: x => x.id_empleado,
                        principalTable: "usuarios",
                        principalColumn: "id_usuario",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "fk_comprobantes_compra_proveedor",
                        columns: x => new { x.id_proveedor, x.id_tenant },
                        principalTable: "proveedores",
                        principalColumns: new[] { "id_proveedor", "id_tenant" },
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "fk_comprobantes_compra_punto_venta",
                        columns: x => new { x.id_punto_venta, x.id_tenant },
                        principalTable: "puntos_venta",
                        principalColumns: new[] { "id_punto_venta", "id_tenant" },
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "fk_comprobantes_compra_tenant",
                        column: x => x.id_tenant,
                        principalTable: "tenants",
                        principalColumn: "id_tenant",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "fk_comprobantes_compra_tipo_comprobante",
                        column: x => x.id_tipo_comprobante,
                        principalTable: "tipos_comprobante",
                        principalColumn: "id_tipo_comprobante",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "items_comprobante_compra",
                columns: table => new
                {
                    id_item = table.Column<int>(type: "integer", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    id_comprobante_compra = table.Column<int>(type: "integer", nullable: false),
                    orden = table.Column<int>(type: "integer", nullable: false),
                    id_articulo = table.Column<int>(type: "integer", nullable: false),
                    descripcion = table.Column<string>(type: "text", nullable: false),
                    cantidad = table.Column<decimal>(type: "numeric(12,3)", nullable: false),
                    bultos = table.Column<decimal>(type: "numeric(10,2)", nullable: true),
                    unidades_por_bulto = table.Column<decimal>(type: "numeric(10,2)", nullable: true),
                    costo_unitario = table.Column<decimal>(type: "numeric(14,4)", nullable: false),
                    descuento = table.Column<decimal>(type: "numeric(14,2)", nullable: false, defaultValue: 0m),
                    id_alicuota_iva = table.Column<int>(type: "integer", nullable: false),
                    porcentaje_iva = table.Column<decimal>(type: "numeric(5,2)", nullable: false),
                    total = table.Column<decimal>(type: "numeric(14,2)", nullable: false),
                    actualiza_costo = table.Column<bool>(type: "boolean", nullable: false, defaultValue: true),
                    precio_sugerido = table.Column<decimal>(type: "numeric(14,2)", nullable: true),
                    created_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    updated_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    deleted_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    id_tenant = table.Column<int>(type: "integer", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_items_comprobante_compra", x => x.id_item);
                    table.CheckConstraint("ck_items_comprobante_compra_cantidad_positiva", "cantidad > 0");
                    table.CheckConstraint("ck_items_comprobante_compra_costo_no_negativo", "costo_unitario >= 0");
                    table.CheckConstraint("ck_items_comprobante_compra_importes_no_negativos", "descuento >= 0 AND total >= 0");
                    table.ForeignKey(
                        name: "fk_items_comprobante_compra_alicuota_iva",
                        column: x => x.id_alicuota_iva,
                        principalTable: "alicuotas_iva",
                        principalColumn: "id_alicuota_iva",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "fk_items_comprobante_compra_articulo",
                        columns: x => new { x.id_articulo, x.id_tenant },
                        principalTable: "articulos",
                        principalColumns: new[] { "id_articulo", "id_tenant" },
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "fk_items_comprobante_compra_comprobante",
                        columns: x => new { x.id_comprobante_compra, x.id_tenant },
                        principalTable: "comprobantes_compra",
                        principalColumns: new[] { "id_comprobante_compra", "id_tenant" },
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "fk_items_comprobante_compra_tenant",
                        column: x => x.id_tenant,
                        principalTable: "tenants",
                        principalColumn: "id_tenant",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateIndex(
                name: "ix_movimientos_stock_comprobante_compra",
                table: "movimientos_stock",
                columns: new[] { "id_comprobante_compra", "id_tenant" });

            migrationBuilder.CreateIndex(
                name: "ix_gastos_comprobante_compra",
                table: "gastos",
                columns: new[] { "id_comprobante_compra", "id_tenant" });

            migrationBuilder.CreateIndex(
                name: "ix_comprobantes_compra_empleado",
                table: "comprobantes_compra",
                column: "id_empleado");

            migrationBuilder.CreateIndex(
                name: "ix_comprobantes_compra_proveedor",
                table: "comprobantes_compra",
                columns: new[] { "id_proveedor", "id_tenant" });

            migrationBuilder.CreateIndex(
                name: "ix_comprobantes_compra_punto_venta_fecha",
                table: "comprobantes_compra",
                columns: new[] { "id_punto_venta", "id_tenant", "fecha_recepcion" });

            migrationBuilder.CreateIndex(
                name: "ix_comprobantes_compra_tenant",
                table: "comprobantes_compra",
                column: "id_tenant");

            migrationBuilder.CreateIndex(
                name: "ix_comprobantes_compra_tipo_comprobante",
                table: "comprobantes_compra",
                column: "id_tipo_comprobante");

            migrationBuilder.CreateIndex(
                name: "ux_comprobantes_compra_numero_externo",
                table: "comprobantes_compra",
                columns: new[] { "id_tenant", "id_proveedor", "id_tipo_comprobante", "numero_externo" },
                unique: true,
                filter: "estado <> 'anulada' AND numero_externo IS NOT NULL");

            migrationBuilder.CreateIndex(
                name: "ix_items_comprobante_compra_alicuota_iva",
                table: "items_comprobante_compra",
                column: "id_alicuota_iva");

            migrationBuilder.CreateIndex(
                name: "ix_items_comprobante_compra_articulo",
                table: "items_comprobante_compra",
                columns: new[] { "id_articulo", "id_tenant" });

            migrationBuilder.CreateIndex(
                name: "ix_items_comprobante_compra_comprobante",
                table: "items_comprobante_compra",
                columns: new[] { "id_comprobante_compra", "id_tenant" });

            migrationBuilder.CreateIndex(
                name: "ix_items_comprobante_compra_tenant",
                table: "items_comprobante_compra",
                column: "id_tenant");

            migrationBuilder.CreateIndex(
                name: "ux_items_comprobante_compra_orden",
                table: "items_comprobante_compra",
                columns: new[] { "id_comprobante_compra", "orden" },
                unique: true);

            migrationBuilder.AddForeignKey(
                name: "fk_gastos_comprobante_compra",
                table: "gastos",
                columns: new[] { "id_comprobante_compra", "id_tenant" },
                principalTable: "comprobantes_compra",
                principalColumns: new[] { "id_comprobante_compra", "id_tenant" },
                onDelete: ReferentialAction.Restrict);

            migrationBuilder.AddForeignKey(
                name: "fk_movimientos_stock_comprobante_compra",
                table: "movimientos_stock",
                columns: new[] { "id_comprobante_compra", "id_tenant" },
                principalTable: "comprobantes_compra",
                principalColumns: new[] { "id_comprobante_compra", "id_tenant" },
                onDelete: ReferentialAction.Restrict);

            // RLS (ADR-4/ADR-15, DB CHANGE GATE exercised in autonomous mode 2026-08-05): cada
            // tabla nueva activa su policy en la misma migración que la crea (design:
            // Migration/Rollout).
            migrationBuilder.HabilitarRlsDeTenant("comprobantes_compra");
            migrationBuilder.HabilitarRlsDeTenant("items_comprobante_compra");

            // design: Table Shapes — E (mismo patrón que CuentaCorrienteEtapa7 con RC): el seed
            // de InicializadorDeBaseDeDatos.SembrarCatalogosFiscalesAsync solo siembra
            // tipos_comprobante cuando la tabla está VACÍA — una base ya migrada desde stage 7
            // en adelante nunca recibiría los tres tipos de compra de otro modo. El guard "AND
            // EXISTS (SELECT 1 FROM tipos_comprobante)" es la lección de stage 7: sin él, este
            // INSERT deja filas en una base GENUINAMENTE vacía durante la migración, y el
            // chequeo de "tabla vacía" del seeder pasa a verla como no-vacía, saltándose las
            // demás filas de TiposComprobanteBase por completo. Con el guard, este INSERT solo
            // corre contra una base que YA tenía catálogo — el escenario real que hay que cubrir
            // (una base migrada desde antes de esta etapa) — nunca contra una recién creada, que
            // queda intacta para que el seeder la puebla completa y atómica (con los tres C-*
            // ya incluidos en TiposComprobanteBase).
            migrationBuilder.Sql(
                """
                INSERT INTO tipos_comprobante (clase, codigo, nombre, letra, signo, discrimina_iva, es_fiscal, afecta_stock, activo, created_at, updated_at)
                SELECT 'compra', v.codigo, v.nombre, v.letra, v.signo, v.discrimina_iva, false, true, true, now(), now()
                FROM (VALUES
                    ('C-FA', 'Factura A de compra', 'A', 1::smallint, true),
                    ('C-FB', 'Factura B de compra', 'B', 1::smallint, false),
                    ('C-FC', 'Factura C de compra', 'C', 1::smallint, false)
                ) AS v(codigo, nombre, letra, signo, discrimina_iva)
                WHERE EXISTS (SELECT 1 FROM tipos_comprobante)
                  AND NOT EXISTS (SELECT 1 FROM tipos_comprobante WHERE codigo = v.codigo);
                """);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            // design: Migration/Rollout — desactiva en vez de borrar, para que una compra ya
            // confirmada (y sus movimientos) sigan siendo legibles después de un rollback.
            // Mismo criterio que CuentaCorrienteEtapa7 con RC.
            migrationBuilder.Sql("UPDATE tipos_comprobante SET activo = false WHERE codigo IN ('C-FA', 'C-FB', 'C-FC');");

            migrationBuilder.DropForeignKey(
                name: "fk_gastos_comprobante_compra",
                table: "gastos");

            migrationBuilder.DropForeignKey(
                name: "fk_movimientos_stock_comprobante_compra",
                table: "movimientos_stock");

            migrationBuilder.DropTable(
                name: "items_comprobante_compra");

            migrationBuilder.DropTable(
                name: "comprobantes_compra");

            migrationBuilder.DropIndex(
                name: "ix_movimientos_stock_comprobante_compra",
                table: "movimientos_stock");

            migrationBuilder.DropIndex(
                name: "ix_gastos_comprobante_compra",
                table: "gastos");

            migrationBuilder.DropColumn(
                name: "id_comprobante_compra",
                table: "movimientos_stock");

            migrationBuilder.DropColumn(
                name: "id_comprobante_compra",
                table: "gastos");

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
                .OldAnnotation("Npgsql:Enum:categoria_gasto", "impuestos,otros,proveedor,servicios,sueldos,viaticos")
                .OldAnnotation("Npgsql:Enum:clase_comprobante", "compra,venta")
                .OldAnnotation("Npgsql:Enum:comportamiento_medio_pago", "cuenta_corriente,efectivo,electronico")
                .OldAnnotation("Npgsql:Enum:estado_compra", "anulada,borrador,confirmada")
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
