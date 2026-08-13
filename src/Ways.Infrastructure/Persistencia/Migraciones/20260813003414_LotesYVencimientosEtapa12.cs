using System;
using Microsoft.EntityFrameworkCore.Migrations;
using Npgsql.EntityFrameworkCore.PostgreSQL.Metadata;
using Ways.Infrastructure.Multitenancy;

#nullable disable

namespace Ways.Infrastructure.Persistencia.Migraciones
{
    /// <inheritdoc />
    public partial class LotesYVencimientosEtapa12 : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            // Orden de statements per design (binding): 1) AlterDatabase (diff de enum) primero
            // — PG permite ADD VALUE dentro de una transacción pero prohíbe USAR el valor nuevo
            // en esa misma transacción, así que ningún Sql() de esta migración puede nombrar
            // 'decomiso'/'reclasificacion'. 2) CreateTable lotes. 3) CreateTable stock_lotes.
            // 4) AddColumn ×6 con sus FKs/índices. 5) HabilitarRlsDeTenant sobre las dos tablas
            // nuevas, al final (RLS necesita que la tabla ya exista).
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
                .Annotation("Npgsql:Enum:motivo_stock", "ajuste,anulacion,compra,decomiso,inventario,reclasificacion,transferencia,venta")
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

            migrationBuilder.CreateTable(
                name: "lotes",
                columns: table => new
                {
                    id_lote = table.Column<int>(type: "integer", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    id_articulo = table.Column<int>(type: "integer", nullable: false),
                    codigo = table.Column<string>(type: "text", nullable: false),
                    fecha_vencimiento = table.Column<DateOnly>(type: "date", nullable: true),
                    es_sin_identificar = table.Column<bool>(type: "boolean", nullable: false, defaultValue: false),
                    created_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    updated_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    deleted_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    id_tenant = table.Column<int>(type: "integer", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_lotes", x => x.id_lote);
                    table.UniqueConstraint("ux_lotes_id_articulo_tenant", x => new { x.id_lote, x.id_articulo, x.id_tenant });
                    table.CheckConstraint("ck_lotes_codigo_no_vacio", "length(btrim(codigo)) > 0");
                    table.CheckConstraint("ck_lotes_vencimiento_segun_tipo", "(es_sin_identificar AND fecha_vencimiento IS NULL) OR (NOT es_sin_identificar AND fecha_vencimiento IS NOT NULL)");
                    table.ForeignKey(
                        name: "fk_lotes_articulo",
                        columns: x => new { x.id_articulo, x.id_tenant },
                        principalTable: "articulos",
                        principalColumns: new[] { "id_articulo", "id_tenant" },
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "fk_lotes_tenant",
                        column: x => x.id_tenant,
                        principalTable: "tenants",
                        principalColumn: "id_tenant",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "stock_lotes",
                columns: table => new
                {
                    id_articulo = table.Column<int>(type: "integer", nullable: false),
                    id_punto_venta = table.Column<int>(type: "integer", nullable: false),
                    id_lote = table.Column<int>(type: "integer", nullable: false),
                    id_tenant = table.Column<int>(type: "integer", nullable: false),
                    cantidad = table.Column<decimal>(type: "numeric(12,3)", nullable: false, defaultValue: 0m)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_stock_lotes", x => new { x.id_articulo, x.id_punto_venta, x.id_lote });
                    table.ForeignKey(
                        name: "fk_stock_lotes_lote",
                        columns: x => new { x.id_lote, x.id_articulo, x.id_tenant },
                        principalTable: "lotes",
                        principalColumns: new[] { "id_lote", "id_articulo", "id_tenant" },
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "fk_stock_lotes_punto_venta",
                        columns: x => new { x.id_punto_venta, x.id_tenant },
                        principalTable: "puntos_venta",
                        principalColumns: new[] { "id_punto_venta", "id_tenant" },
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "fk_stock_lotes_tenant",
                        column: x => x.id_tenant,
                        principalTable: "tenants",
                        principalColumn: "id_tenant",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.AddColumn<int>(
                name: "id_lote",
                table: "movimientos_stock",
                type: "integer",
                nullable: true);

            migrationBuilder.AddColumn<int>(
                name: "id_lote",
                table: "items_comprobante_venta",
                type: "integer",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "codigo_lote",
                table: "items_comprobante_compra",
                type: "text",
                nullable: true);

            migrationBuilder.AddColumn<DateOnly>(
                name: "fecha_vencimiento",
                table: "items_comprobante_compra",
                type: "date",
                nullable: true);

            migrationBuilder.AddColumn<int>(
                name: "id_lote",
                table: "items_comprobante_compra",
                type: "integer",
                nullable: true);

            migrationBuilder.AddColumn<bool>(
                name: "controla_lote",
                table: "articulos",
                type: "boolean",
                nullable: false,
                defaultValue: false);

            migrationBuilder.CreateIndex(
                name: "ix_movimientos_stock_lote",
                table: "movimientos_stock",
                columns: new[] { "id_lote", "id_articulo", "id_tenant" });

            migrationBuilder.CreateIndex(
                name: "ix_items_comprobante_venta_lote",
                table: "items_comprobante_venta",
                columns: new[] { "id_lote", "id_articulo", "id_tenant" });

            migrationBuilder.CreateIndex(
                name: "ix_items_comprobante_compra_lote",
                table: "items_comprobante_compra",
                columns: new[] { "id_lote", "id_articulo", "id_tenant" });

            migrationBuilder.AddCheckConstraint(
                name: "ck_items_comprobante_compra_lote_input",
                table: "items_comprobante_compra",
                sql: "(codigo_lote IS NULL AND fecha_vencimiento IS NULL) OR fecha_vencimiento IS NOT NULL");

            migrationBuilder.CreateIndex(
                name: "ix_articulos_controla_lote",
                table: "articulos",
                column: "id_tenant",
                filter: "controla_lote AND deleted_at IS NULL");

            migrationBuilder.CreateIndex(
                name: "ix_lotes_articulo",
                table: "lotes",
                columns: new[] { "id_articulo", "id_tenant" });

            migrationBuilder.CreateIndex(
                name: "ix_lotes_tenant",
                table: "lotes",
                column: "id_tenant");

            migrationBuilder.CreateIndex(
                name: "ix_lotes_vencimiento",
                table: "lotes",
                columns: new[] { "id_tenant", "fecha_vencimiento" },
                filter: "deleted_at IS NULL");

            migrationBuilder.CreateIndex(
                name: "ux_lotes_articulo_codigo",
                table: "lotes",
                columns: new[] { "id_tenant", "id_articulo", "codigo" },
                unique: true,
                filter: "deleted_at IS NULL");

            migrationBuilder.CreateIndex(
                name: "ux_lotes_sin_identificar",
                table: "lotes",
                columns: new[] { "id_tenant", "id_articulo" },
                unique: true,
                filter: "es_sin_identificar AND deleted_at IS NULL");

            migrationBuilder.CreateIndex(
                name: "ix_stock_lotes_lote",
                table: "stock_lotes",
                columns: new[] { "id_lote", "id_articulo", "id_tenant" });

            migrationBuilder.CreateIndex(
                name: "ix_stock_lotes_punto_venta",
                table: "stock_lotes",
                columns: new[] { "id_punto_venta", "id_tenant" });

            migrationBuilder.CreateIndex(
                name: "ix_stock_lotes_tenant",
                table: "stock_lotes",
                column: "id_tenant");

            migrationBuilder.AddForeignKey(
                name: "fk_items_comprobante_compra_lote",
                table: "items_comprobante_compra",
                columns: new[] { "id_lote", "id_articulo", "id_tenant" },
                principalTable: "lotes",
                principalColumns: new[] { "id_lote", "id_articulo", "id_tenant" },
                onDelete: ReferentialAction.Restrict);

            migrationBuilder.AddForeignKey(
                name: "fk_items_comprobante_venta_lote",
                table: "items_comprobante_venta",
                columns: new[] { "id_lote", "id_articulo", "id_tenant" },
                principalTable: "lotes",
                principalColumns: new[] { "id_lote", "id_articulo", "id_tenant" },
                onDelete: ReferentialAction.Restrict);

            migrationBuilder.AddForeignKey(
                name: "fk_movimientos_stock_lote",
                table: "movimientos_stock",
                columns: new[] { "id_lote", "id_articulo", "id_tenant" },
                principalTable: "lotes",
                principalColumns: new[] { "id_lote", "id_articulo", "id_tenant" },
                onDelete: ReferentialAction.Restrict);

            // RLS al final (ADR-15): las dos tablas nuevas ya existen acá, misma migración que
            // las crea — nunca una ventana con la tabla scopeada y sin policy.
            migrationBuilder.HabilitarRlsDeTenant("lotes");
            migrationBuilder.HabilitarRlsDeTenant("stock_lotes");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropForeignKey(
                name: "fk_items_comprobante_compra_lote",
                table: "items_comprobante_compra");

            migrationBuilder.DropForeignKey(
                name: "fk_items_comprobante_venta_lote",
                table: "items_comprobante_venta");

            migrationBuilder.DropForeignKey(
                name: "fk_movimientos_stock_lote",
                table: "movimientos_stock");

            migrationBuilder.DropTable(
                name: "stock_lotes");

            migrationBuilder.DropTable(
                name: "lotes");

            migrationBuilder.DropIndex(
                name: "ix_movimientos_stock_lote",
                table: "movimientos_stock");

            migrationBuilder.DropIndex(
                name: "ix_items_comprobante_venta_lote",
                table: "items_comprobante_venta");

            migrationBuilder.DropIndex(
                name: "ix_items_comprobante_compra_lote",
                table: "items_comprobante_compra");

            migrationBuilder.DropCheckConstraint(
                name: "ck_items_comprobante_compra_lote_input",
                table: "items_comprobante_compra");

            migrationBuilder.DropIndex(
                name: "ix_articulos_controla_lote",
                table: "articulos");

            migrationBuilder.DropColumn(
                name: "id_lote",
                table: "movimientos_stock");

            migrationBuilder.DropColumn(
                name: "id_lote",
                table: "items_comprobante_venta");

            migrationBuilder.DropColumn(
                name: "codigo_lote",
                table: "items_comprobante_compra");

            migrationBuilder.DropColumn(
                name: "fecha_vencimiento",
                table: "items_comprobante_compra");

            migrationBuilder.DropColumn(
                name: "id_lote",
                table: "items_comprobante_compra");

            migrationBuilder.DropColumn(
                name: "controla_lote",
                table: "articulos");

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
                .OldAnnotation("Npgsql:Enum:estado_compra", "anulada,borrador,confirmada")
                .OldAnnotation("Npgsql:Enum:estado_comprobante", "anulado,emitido")
                .OldAnnotation("Npgsql:Enum:estado_tenant", "activo,baja,suspendido")
                .OldAnnotation("Npgsql:Enum:estado_turno", "abierto,cerrado")
                .OldAnnotation("Npgsql:Enum:estado_usuario", "activo,bloqueado,inactivo")
                .OldAnnotation("Npgsql:Enum:modo_lista", "derivada,fija")
                .OldAnnotation("Npgsql:Enum:motivo_stock", "ajuste,anulacion,compra,decomiso,inventario,reclasificacion,transferencia,venta")
                .OldAnnotation("Npgsql:Enum:tipo_documento", "cuil,cuit,dni,otro,pasaporte")
                .OldAnnotation("Npgsql:Enum:tipo_movimiento_caja", "apertura_cajon,refuerzo,retiro")
                .OldAnnotation("Npgsql:Enum:tipo_movimiento_cc", "actualizacion_precios,ajuste,consumo,pago")
                .OldAnnotation("Npgsql:Enum:tipo_movimiento_tesoreria", "ajuste,deposito,gasto,retiro_caja")
                .OldAnnotation("Npgsql:Enum:unidad_venta", "peso,unidad")
                .OldAnnotation("Npgsql:PostgresExtension:citext", ",,");
        }
    }
}
