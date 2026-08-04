using System;
using Microsoft.EntityFrameworkCore.Migrations;
using Npgsql.EntityFrameworkCore.PostgreSQL.Metadata;
using Ways.Domain.CuentaCorriente;
using Ways.Domain.Stock;
using Ways.Domain.Ventas;
using Ways.Infrastructure.Multitenancy;

#nullable disable

namespace Ways.Infrastructure.Persistencia.Migraciones
{
    /// <inheritdoc />
    public partial class VentasStockYCuentaCorrienteEtapa5 : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
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
                .OldAnnotation("Npgsql:Enum:clase_comprobante", "compra,venta")
                .OldAnnotation("Npgsql:Enum:comportamiento_medio_pago", "cuenta_corriente,efectivo,electronico")
                .OldAnnotation("Npgsql:Enum:estado_tenant", "activo,baja,suspendido")
                .OldAnnotation("Npgsql:Enum:estado_usuario", "activo,bloqueado,inactivo")
                .OldAnnotation("Npgsql:Enum:modo_lista", "derivada,fija")
                .OldAnnotation("Npgsql:Enum:tipo_documento", "cuil,cuit,dni,otro,pasaporte")
                .OldAnnotation("Npgsql:Enum:unidad_venta", "peso,unidad")
                .OldAnnotation("Npgsql:PostgresExtension:citext", ",,");

            migrationBuilder.AddUniqueConstraint(
                name: "ak_medios_pago_id_medio_pago_id_tenant",
                table: "medios_pago",
                columns: new[] { "id_medio_pago", "id_tenant" });

            migrationBuilder.AddUniqueConstraint(
                name: "ak_clientes_id_cliente_id_tenant",
                table: "clientes",
                columns: new[] { "id_cliente", "id_tenant" });

            migrationBuilder.CreateTable(
                name: "comprobantes_venta",
                columns: table => new
                {
                    id_comprobante_venta = table.Column<int>(type: "integer", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    id_tipo_comprobante = table.Column<int>(type: "integer", nullable: false),
                    numero = table.Column<long>(type: "bigint", nullable: false),
                    fecha = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    id_punto_venta = table.Column<int>(type: "integer", nullable: false),
                    id_turno_caja = table.Column<int>(type: "integer", nullable: true),
                    id_empleado = table.Column<int>(type: "integer", nullable: false),
                    id_cliente = table.Column<int>(type: "integer", nullable: false),
                    id_comprobante_asociado = table.Column<int>(type: "integer", nullable: true),
                    subtotal = table.Column<decimal>(type: "numeric(14,2)", nullable: false),
                    descuento_total = table.Column<decimal>(type: "numeric(14,2)", nullable: false),
                    total = table.Column<decimal>(type: "numeric(14,2)", nullable: false),
                    neto_gravado = table.Column<decimal>(type: "numeric(14,2)", nullable: true),
                    iva_total = table.Column<decimal>(type: "numeric(14,2)", nullable: true),
                    direccion_entrega = table.Column<string>(type: "text", nullable: true),
                    observaciones = table.Column<string>(type: "text", nullable: true),
                    estado = table.Column<EstadoComprobante>(type: "estado_comprobante", nullable: false),
                    created_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    updated_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    deleted_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    id_tenant = table.Column<int>(type: "integer", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_comprobantes_venta", x => x.id_comprobante_venta);
                    table.UniqueConstraint("ak_comprobantes_venta_id_comprobante_venta_id_tenant", x => new { x.id_comprobante_venta, x.id_tenant });
                    table.CheckConstraint("ck_comprobantes_venta_numero_positivo", "numero > 0");
                    table.ForeignKey(
                        name: "fk_comprobantes_venta_cliente",
                        columns: x => new { x.id_cliente, x.id_tenant },
                        principalTable: "clientes",
                        principalColumns: new[] { "id_cliente", "id_tenant" },
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "fk_comprobantes_venta_comprobante_asociado",
                        columns: x => new { x.id_comprobante_asociado, x.id_tenant },
                        principalTable: "comprobantes_venta",
                        principalColumns: new[] { "id_comprobante_venta", "id_tenant" },
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "fk_comprobantes_venta_empleado",
                        column: x => x.id_empleado,
                        principalTable: "usuarios",
                        principalColumn: "id_usuario",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "fk_comprobantes_venta_punto_venta",
                        columns: x => new { x.id_punto_venta, x.id_tenant },
                        principalTable: "puntos_venta",
                        principalColumns: new[] { "id_punto_venta", "id_tenant" },
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "fk_comprobantes_venta_tenant",
                        column: x => x.id_tenant,
                        principalTable: "tenants",
                        principalColumn: "id_tenant",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "fk_comprobantes_venta_tipo_comprobante",
                        column: x => x.id_tipo_comprobante,
                        principalTable: "tipos_comprobante",
                        principalColumn: "id_tipo_comprobante",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "stock",
                columns: table => new
                {
                    id_articulo = table.Column<int>(type: "integer", nullable: false),
                    id_punto_venta = table.Column<int>(type: "integer", nullable: false),
                    id_tenant = table.Column<int>(type: "integer", nullable: false),
                    cantidad = table.Column<decimal>(type: "numeric(12,3)", nullable: false, defaultValue: 0m),
                    minimo = table.Column<decimal>(type: "numeric(12,3)", nullable: true),
                    reposicion = table.Column<decimal>(type: "numeric(12,3)", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_stock", x => new { x.id_articulo, x.id_punto_venta });
                    table.ForeignKey(
                        name: "fk_stock_articulo",
                        columns: x => new { x.id_articulo, x.id_tenant },
                        principalTable: "articulos",
                        principalColumns: new[] { "id_articulo", "id_tenant" },
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "fk_stock_punto_venta",
                        columns: x => new { x.id_punto_venta, x.id_tenant },
                        principalTable: "puntos_venta",
                        principalColumns: new[] { "id_punto_venta", "id_tenant" },
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "fk_stock_tenant",
                        column: x => x.id_tenant,
                        principalTable: "tenants",
                        principalColumn: "id_tenant",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "items_comprobante_venta",
                columns: table => new
                {
                    id_item = table.Column<int>(type: "integer", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    id_comprobante_venta = table.Column<int>(type: "integer", nullable: false),
                    orden = table.Column<int>(type: "integer", nullable: false),
                    id_articulo = table.Column<int>(type: "integer", nullable: true),
                    descripcion = table.Column<string>(type: "text", nullable: false),
                    codigo_barra = table.Column<string>(type: "text", nullable: true),
                    id_area = table.Column<int>(type: "integer", nullable: false),
                    id_lista_precio = table.Column<int>(type: "integer", nullable: false),
                    id_oferta = table.Column<int>(type: "integer", nullable: true),
                    id_alicuota_iva = table.Column<int>(type: "integer", nullable: false),
                    porcentaje_iva = table.Column<decimal>(type: "numeric(5,2)", nullable: false),
                    cantidad = table.Column<decimal>(type: "numeric(12,3)", nullable: false),
                    precio_unitario = table.Column<decimal>(type: "numeric(14,2)", nullable: false),
                    descuento = table.Column<decimal>(type: "numeric(14,2)", nullable: false, defaultValue: 0m),
                    total = table.Column<decimal>(type: "numeric(14,2)", nullable: false),
                    created_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    updated_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    deleted_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    id_tenant = table.Column<int>(type: "integer", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_items_comprobante_venta", x => x.id_item);
                    table.ForeignKey(
                        name: "fk_items_comprobante_venta_alicuota_iva",
                        column: x => x.id_alicuota_iva,
                        principalTable: "alicuotas_iva",
                        principalColumn: "id_alicuota_iva",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "fk_items_comprobante_venta_area",
                        columns: x => new { x.id_area, x.id_tenant },
                        principalTable: "areas",
                        principalColumns: new[] { "id_area", "id_tenant" },
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "fk_items_comprobante_venta_articulo",
                        columns: x => new { x.id_articulo, x.id_tenant },
                        principalTable: "articulos",
                        principalColumns: new[] { "id_articulo", "id_tenant" },
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "fk_items_comprobante_venta_comprobante",
                        columns: x => new { x.id_comprobante_venta, x.id_tenant },
                        principalTable: "comprobantes_venta",
                        principalColumns: new[] { "id_comprobante_venta", "id_tenant" },
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "fk_items_comprobante_venta_lista_precio",
                        columns: x => new { x.id_lista_precio, x.id_tenant },
                        principalTable: "listas_precio",
                        principalColumns: new[] { "id_lista_precio", "id_tenant" },
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "fk_items_comprobante_venta_oferta",
                        columns: x => new { x.id_oferta, x.id_tenant },
                        principalTable: "ofertas",
                        principalColumns: new[] { "id_oferta", "id_tenant" },
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "fk_items_comprobante_venta_tenant",
                        column: x => x.id_tenant,
                        principalTable: "tenants",
                        principalColumn: "id_tenant",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "movimientos_stock",
                columns: table => new
                {
                    id_movimiento = table.Column<int>(type: "integer", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    id_tenant = table.Column<int>(type: "integer", nullable: false),
                    id_articulo = table.Column<int>(type: "integer", nullable: false),
                    id_punto_venta = table.Column<int>(type: "integer", nullable: false),
                    cantidad = table.Column<decimal>(type: "numeric(12,3)", nullable: false),
                    motivo = table.Column<MotivoStock>(type: "motivo_stock", nullable: false),
                    id_comprobante_venta = table.Column<int>(type: "integer", nullable: true),
                    id_punto_venta_destino = table.Column<int>(type: "integer", nullable: true),
                    id_empleado = table.Column<int>(type: "integer", nullable: false),
                    observaciones = table.Column<string>(type: "text", nullable: true),
                    creado_el = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_movimientos_stock", x => x.id_movimiento);
                    table.CheckConstraint("ck_movimientos_stock_cantidad_no_cero", "cantidad <> 0");
                    table.ForeignKey(
                        name: "fk_movimientos_stock_articulo",
                        columns: x => new { x.id_articulo, x.id_tenant },
                        principalTable: "articulos",
                        principalColumns: new[] { "id_articulo", "id_tenant" },
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "fk_movimientos_stock_comprobante_venta",
                        columns: x => new { x.id_comprobante_venta, x.id_tenant },
                        principalTable: "comprobantes_venta",
                        principalColumns: new[] { "id_comprobante_venta", "id_tenant" },
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "fk_movimientos_stock_empleado",
                        column: x => x.id_empleado,
                        principalTable: "usuarios",
                        principalColumn: "id_usuario",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "fk_movimientos_stock_punto_venta",
                        columns: x => new { x.id_punto_venta, x.id_tenant },
                        principalTable: "puntos_venta",
                        principalColumns: new[] { "id_punto_venta", "id_tenant" },
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "fk_movimientos_stock_punto_venta_destino",
                        columns: x => new { x.id_punto_venta_destino, x.id_tenant },
                        principalTable: "puntos_venta",
                        principalColumns: new[] { "id_punto_venta", "id_tenant" },
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "fk_movimientos_stock_tenant",
                        column: x => x.id_tenant,
                        principalTable: "tenants",
                        principalColumn: "id_tenant",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "pagos_comprobante",
                columns: table => new
                {
                    id_pago = table.Column<int>(type: "integer", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    id_comprobante_venta = table.Column<int>(type: "integer", nullable: false),
                    id_medio_pago = table.Column<int>(type: "integer", nullable: false),
                    importe = table.Column<decimal>(type: "numeric(14,2)", nullable: false),
                    referencia = table.Column<string>(type: "text", nullable: true),
                    vuelto = table.Column<decimal>(type: "numeric(14,2)", nullable: false, defaultValue: 0m),
                    created_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    updated_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    deleted_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    id_tenant = table.Column<int>(type: "integer", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_pagos_comprobante", x => x.id_pago);
                    table.UniqueConstraint("ak_pagos_comprobante_id_pago_id_tenant", x => new { x.id_pago, x.id_tenant });
                    table.CheckConstraint("ck_pagos_comprobante_vuelto_no_negativo", "vuelto >= 0");
                    table.ForeignKey(
                        name: "fk_pagos_comprobante_comprobante",
                        columns: x => new { x.id_comprobante_venta, x.id_tenant },
                        principalTable: "comprobantes_venta",
                        principalColumns: new[] { "id_comprobante_venta", "id_tenant" },
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "fk_pagos_comprobante_medio_pago",
                        columns: x => new { x.id_medio_pago, x.id_tenant },
                        principalTable: "medios_pago",
                        principalColumns: new[] { "id_medio_pago", "id_tenant" },
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "fk_pagos_comprobante_tenant",
                        column: x => x.id_tenant,
                        principalTable: "tenants",
                        principalColumn: "id_tenant",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "movimientos_cuenta_corriente",
                columns: table => new
                {
                    id_movimiento = table.Column<int>(type: "integer", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    id_tenant = table.Column<int>(type: "integer", nullable: false),
                    id_cliente = table.Column<int>(type: "integer", nullable: false),
                    fecha = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    id_punto_venta = table.Column<int>(type: "integer", nullable: false),
                    id_empleado = table.Column<int>(type: "integer", nullable: false),
                    tipo = table.Column<TipoMovimientoCc>(type: "tipo_movimiento_cc", nullable: false),
                    id_comprobante_venta = table.Column<int>(type: "integer", nullable: true),
                    id_pago_comprobante = table.Column<int>(type: "integer", nullable: true),
                    importe = table.Column<decimal>(type: "numeric(14,2)", nullable: false),
                    saldo_resultante = table.Column<decimal>(type: "numeric(14,2)", nullable: false),
                    detalle = table.Column<string>(type: "text", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_movimientos_cuenta_corriente", x => x.id_movimiento);
                    table.ForeignKey(
                        name: "fk_movimientos_cuenta_corriente_cliente",
                        columns: x => new { x.id_cliente, x.id_tenant },
                        principalTable: "clientes",
                        principalColumns: new[] { "id_cliente", "id_tenant" },
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "fk_movimientos_cuenta_corriente_comprobante_venta",
                        columns: x => new { x.id_comprobante_venta, x.id_tenant },
                        principalTable: "comprobantes_venta",
                        principalColumns: new[] { "id_comprobante_venta", "id_tenant" },
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "fk_movimientos_cuenta_corriente_empleado",
                        column: x => x.id_empleado,
                        principalTable: "usuarios",
                        principalColumn: "id_usuario",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "fk_movimientos_cuenta_corriente_pago_comprobante",
                        columns: x => new { x.id_pago_comprobante, x.id_tenant },
                        principalTable: "pagos_comprobante",
                        principalColumns: new[] { "id_pago", "id_tenant" },
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "fk_movimientos_cuenta_corriente_punto_venta",
                        columns: x => new { x.id_punto_venta, x.id_tenant },
                        principalTable: "puntos_venta",
                        principalColumns: new[] { "id_punto_venta", "id_tenant" },
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "fk_movimientos_cuenta_corriente_tenant",
                        column: x => x.id_tenant,
                        principalTable: "tenants",
                        principalColumn: "id_tenant",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateIndex(
                name: "ix_comprobantes_venta_asociado",
                table: "comprobantes_venta",
                columns: new[] { "id_comprobante_asociado", "id_tenant" });

            migrationBuilder.CreateIndex(
                name: "ix_comprobantes_venta_cliente",
                table: "comprobantes_venta",
                columns: new[] { "id_cliente", "id_tenant" });

            migrationBuilder.CreateIndex(
                name: "ix_comprobantes_venta_empleado",
                table: "comprobantes_venta",
                column: "id_empleado");

            migrationBuilder.CreateIndex(
                name: "ix_comprobantes_venta_punto_venta_fecha",
                table: "comprobantes_venta",
                columns: new[] { "id_punto_venta", "id_tenant", "fecha" });

            migrationBuilder.CreateIndex(
                name: "ix_comprobantes_venta_tenant",
                table: "comprobantes_venta",
                column: "id_tenant");

            migrationBuilder.CreateIndex(
                name: "ix_comprobantes_venta_tipo_comprobante",
                table: "comprobantes_venta",
                column: "id_tipo_comprobante");

            migrationBuilder.CreateIndex(
                name: "ux_comprobantes_venta_numero",
                table: "comprobantes_venta",
                columns: new[] { "id_punto_venta", "id_tipo_comprobante", "numero" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "ix_items_comprobante_venta_alicuota_iva",
                table: "items_comprobante_venta",
                column: "id_alicuota_iva");

            migrationBuilder.CreateIndex(
                name: "ix_items_comprobante_venta_area",
                table: "items_comprobante_venta",
                columns: new[] { "id_area", "id_tenant" });

            migrationBuilder.CreateIndex(
                name: "ix_items_comprobante_venta_articulo",
                table: "items_comprobante_venta",
                columns: new[] { "id_articulo", "id_tenant" });

            migrationBuilder.CreateIndex(
                name: "ix_items_comprobante_venta_comprobante",
                table: "items_comprobante_venta",
                columns: new[] { "id_comprobante_venta", "id_tenant" });

            migrationBuilder.CreateIndex(
                name: "ix_items_comprobante_venta_lista_precio",
                table: "items_comprobante_venta",
                columns: new[] { "id_lista_precio", "id_tenant" });

            migrationBuilder.CreateIndex(
                name: "ix_items_comprobante_venta_oferta",
                table: "items_comprobante_venta",
                columns: new[] { "id_oferta", "id_tenant" });

            migrationBuilder.CreateIndex(
                name: "ix_items_comprobante_venta_tenant",
                table: "items_comprobante_venta",
                column: "id_tenant");

            migrationBuilder.CreateIndex(
                name: "ux_items_comprobante_venta_orden",
                table: "items_comprobante_venta",
                columns: new[] { "id_comprobante_venta", "orden" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "ix_movimientos_cuenta_corriente_cliente_fecha",
                table: "movimientos_cuenta_corriente",
                columns: new[] { "id_cliente", "id_tenant", "fecha" });

            migrationBuilder.CreateIndex(
                name: "ix_movimientos_cuenta_corriente_comprobante_venta",
                table: "movimientos_cuenta_corriente",
                columns: new[] { "id_comprobante_venta", "id_tenant" });

            migrationBuilder.CreateIndex(
                name: "ix_movimientos_cuenta_corriente_empleado",
                table: "movimientos_cuenta_corriente",
                column: "id_empleado");

            migrationBuilder.CreateIndex(
                name: "ix_movimientos_cuenta_corriente_pago_comprobante",
                table: "movimientos_cuenta_corriente",
                columns: new[] { "id_pago_comprobante", "id_tenant" });

            migrationBuilder.CreateIndex(
                name: "ix_movimientos_cuenta_corriente_punto_venta",
                table: "movimientos_cuenta_corriente",
                columns: new[] { "id_punto_venta", "id_tenant" });

            migrationBuilder.CreateIndex(
                name: "ix_movimientos_cuenta_corriente_tenant",
                table: "movimientos_cuenta_corriente",
                column: "id_tenant");

            migrationBuilder.CreateIndex(
                name: "ix_movimientos_stock_articulo",
                table: "movimientos_stock",
                columns: new[] { "id_articulo", "id_tenant" });

            migrationBuilder.CreateIndex(
                name: "ix_movimientos_stock_articulo_punto_venta",
                table: "movimientos_stock",
                columns: new[] { "id_articulo", "id_punto_venta", "id_tenant" });

            migrationBuilder.CreateIndex(
                name: "ix_movimientos_stock_comprobante_venta",
                table: "movimientos_stock",
                columns: new[] { "id_comprobante_venta", "id_tenant" });

            migrationBuilder.CreateIndex(
                name: "ix_movimientos_stock_empleado",
                table: "movimientos_stock",
                column: "id_empleado");

            migrationBuilder.CreateIndex(
                name: "ix_movimientos_stock_punto_venta",
                table: "movimientos_stock",
                columns: new[] { "id_punto_venta", "id_tenant" });

            migrationBuilder.CreateIndex(
                name: "ix_movimientos_stock_punto_venta_destino",
                table: "movimientos_stock",
                columns: new[] { "id_punto_venta_destino", "id_tenant" });

            migrationBuilder.CreateIndex(
                name: "ix_movimientos_stock_tenant",
                table: "movimientos_stock",
                column: "id_tenant");

            migrationBuilder.CreateIndex(
                name: "ix_pagos_comprobante_comprobante",
                table: "pagos_comprobante",
                columns: new[] { "id_comprobante_venta", "id_tenant" });

            migrationBuilder.CreateIndex(
                name: "ix_pagos_comprobante_medio_pago",
                table: "pagos_comprobante",
                columns: new[] { "id_medio_pago", "id_tenant" });

            migrationBuilder.CreateIndex(
                name: "ix_pagos_comprobante_tenant",
                table: "pagos_comprobante",
                column: "id_tenant");

            migrationBuilder.CreateIndex(
                name: "ix_stock_articulo",
                table: "stock",
                columns: new[] { "id_articulo", "id_tenant" });

            migrationBuilder.CreateIndex(
                name: "ix_stock_punto_venta",
                table: "stock",
                columns: new[] { "id_punto_venta", "id_tenant" });

            migrationBuilder.CreateIndex(
                name: "ix_stock_tenant",
                table: "stock",
                column: "id_tenant");

            // RLS (ADR-4/ADR-15, DB CHANGE GATE aprobado 2026-08-04): cada tabla nueva activa
            // su policy en la misma migración que la crea (design: Migration Sequencing).
            migrationBuilder.HabilitarRlsDeTenant("comprobantes_venta");
            migrationBuilder.HabilitarRlsDeTenant("items_comprobante_venta");
            migrationBuilder.HabilitarRlsDeTenant("pagos_comprobante");
            migrationBuilder.HabilitarRlsDeTenant("stock");
            migrationBuilder.HabilitarRlsDeTenant("movimientos_stock");
            migrationBuilder.HabilitarRlsDeTenant("movimientos_cuenta_corriente");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "items_comprobante_venta");

            migrationBuilder.DropTable(
                name: "movimientos_cuenta_corriente");

            migrationBuilder.DropTable(
                name: "movimientos_stock");

            migrationBuilder.DropTable(
                name: "stock");

            migrationBuilder.DropTable(
                name: "pagos_comprobante");

            migrationBuilder.DropTable(
                name: "comprobantes_venta");

            migrationBuilder.DropUniqueConstraint(
                name: "ak_medios_pago_id_medio_pago_id_tenant",
                table: "medios_pago");

            migrationBuilder.DropUniqueConstraint(
                name: "ak_clientes_id_cliente_id_tenant",
                table: "clientes");

            migrationBuilder.AlterDatabase()
                .Annotation("Npgsql:Enum:clase_comprobante", "compra,venta")
                .Annotation("Npgsql:Enum:comportamiento_medio_pago", "cuenta_corriente,efectivo,electronico")
                .Annotation("Npgsql:Enum:estado_tenant", "activo,baja,suspendido")
                .Annotation("Npgsql:Enum:estado_usuario", "activo,bloqueado,inactivo")
                .Annotation("Npgsql:Enum:modo_lista", "derivada,fija")
                .Annotation("Npgsql:Enum:tipo_documento", "cuil,cuit,dni,otro,pasaporte")
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
        }
    }
}
