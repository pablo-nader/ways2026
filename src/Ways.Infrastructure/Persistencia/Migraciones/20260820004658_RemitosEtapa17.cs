using System;
using Microsoft.EntityFrameworkCore.Migrations;
using Npgsql.EntityFrameworkCore.PostgreSQL.Metadata;
using Ways.Domain.Ventas;
using Ways.Infrastructure.Multitenancy;

#nullable disable

namespace Ways.Infrastructure.Persistencia.Migraciones
{
    /// <inheritdoc />
    public partial class RemitosEtapa17 : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            // Orden de statements per gate (binding, proposal.md:1014-1016): 1) AlterDatabase —
            // ESTE statement es quien ejecuta `ALTER TYPE motivo_stock ADD VALUE 'remito'`
            // (noveno valor, IRREVERSIBLE — Postgres no soporta DROP VALUE, mismo mecanismo y
            // misma aceptación que decomiso/reclasificacion de la Etapa 12) seguido de `CREATE
            // TYPE estado_remito` — ningún `Sql()` de ESTA migración puede nombrar 'remito'
            // (Postgres prohíbe usar un valor de enum agregado dentro de la misma transacción
            // que lo agregó, decisión 11). 2) CreateTable remitos + sus índices. 3) CreateTable
            // items_remito + sus índices. 4) AddColumn/AddForeignKey/CreateIndex de
            // movimientos_stock.id_remito. 5) Data statement 2 (TXR guardado). 6)
            // HabilitarRlsDeTenant sobre las dos tablas nuevas, al final.
            //
            // `dotnet ef migrations add` serializa `estado_remito` en orden ALFABÉTICO por
            // defecto (mismo residuo ya documentado en PresupuestosEtapa17/las migraciones de
            // las etapas 15/16) — corregido a mano acá para que el CREATE TYPE resultante sea
            // VERBATIM al gate (task 4.1/4.2, registrado como desvío esperado, no un hallazgo).
            // El AddColumn/CreateIndex/AddForeignKey de movimientos_stock y las 15 CreateIndex de
            // remitos/items_remito también llegaron reordenadas/agrupadas por tabla a mano — EF
            // las emite todas juntas al final, no intercaladas por tabla (mismo hallazgo que
            // PresupuestosEtapa17 task 1.11).
            migrationBuilder.AlterDatabase()
                .Annotation("Npgsql:Enum:categoria_gasto", "impuestos,otros,proveedor,servicios,sueldos,viaticos")
                .Annotation("Npgsql:Enum:clase_comprobante", "compra,venta")
                .Annotation("Npgsql:Enum:comportamiento_medio_pago", "cuenta_corriente,efectivo,electronico")
                .Annotation("Npgsql:Enum:estado_compra", "anulada,borrador,confirmada")
                .Annotation("Npgsql:Enum:estado_comprobante", "anulado,emitido")
                .Annotation("Npgsql:Enum:estado_orden_compra", "anulada,borrador,cerrada,enviada,recibida_parcial")
                .Annotation("Npgsql:Enum:estado_presupuesto", "anulado,borrador,convertido,enviado")
                .Annotation("Npgsql:Enum:estado_remito", "borrador,emitido,facturado,anulado")
                .Annotation("Npgsql:Enum:estado_tenant", "activo,baja,suspendido")
                .Annotation("Npgsql:Enum:estado_turno", "abierto,cerrado")
                .Annotation("Npgsql:Enum:estado_usuario", "activo,bloqueado,inactivo")
                .Annotation("Npgsql:Enum:modo_lista", "derivada,fija")
                .Annotation("Npgsql:Enum:motivo_stock", "ajuste,anulacion,compra,decomiso,inventario,reclasificacion,remito,transferencia,venta")
                .Annotation("Npgsql:Enum:tipo_documento", "cuil,cuit,dni,otro,pasaporte")
                .Annotation("Npgsql:Enum:tipo_movimiento_caja", "apertura_cajon,refuerzo,retiro")
                .Annotation("Npgsql:Enum:tipo_movimiento_cc", "actualizacion_precios,ajuste,consumo,pago")
                .Annotation("Npgsql:Enum:tipo_movimiento_cc_proveedor", "ajuste,apertura,compra,pago")
                .Annotation("Npgsql:Enum:tipo_movimiento_tesoreria", "ajuste,deposito,gasto,retiro_caja")
                .Annotation("Npgsql:Enum:unidad_venta", "peso,unidad")
                .Annotation("Npgsql:PostgresExtension:citext", ",,")
                .OldAnnotation("Npgsql:Enum:categoria_gasto", "impuestos,otros,proveedor,servicios,sueldos,viaticos")
                .OldAnnotation("Npgsql:Enum:clase_comprobante", "compra,venta")
                .OldAnnotation("Npgsql:Enum:comportamiento_medio_pago", "cuenta_corriente,efectivo,electronico")
                .OldAnnotation("Npgsql:Enum:estado_compra", "anulada,borrador,confirmada")
                .OldAnnotation("Npgsql:Enum:estado_comprobante", "anulado,emitido")
                .OldAnnotation("Npgsql:Enum:estado_orden_compra", "anulada,borrador,cerrada,enviada,recibida_parcial")
                .OldAnnotation("Npgsql:Enum:estado_presupuesto", "anulado,borrador,convertido,enviado")
                .OldAnnotation("Npgsql:Enum:estado_tenant", "activo,baja,suspendido")
                .OldAnnotation("Npgsql:Enum:estado_turno", "abierto,cerrado")
                .OldAnnotation("Npgsql:Enum:estado_usuario", "activo,bloqueado,inactivo")
                .OldAnnotation("Npgsql:Enum:modo_lista", "derivada,fija")
                .OldAnnotation("Npgsql:Enum:motivo_stock", "ajuste,anulacion,compra,decomiso,inventario,reclasificacion,transferencia,venta")
                .OldAnnotation("Npgsql:Enum:tipo_documento", "cuil,cuit,dni,otro,pasaporte")
                .OldAnnotation("Npgsql:Enum:tipo_movimiento_caja", "apertura_cajon,refuerzo,retiro")
                .OldAnnotation("Npgsql:Enum:tipo_movimiento_cc", "actualizacion_precios,ajuste,consumo,pago")
                .OldAnnotation("Npgsql:Enum:tipo_movimiento_cc_proveedor", "ajuste,apertura,compra,pago")
                .OldAnnotation("Npgsql:Enum:tipo_movimiento_tesoreria", "ajuste,deposito,gasto,retiro_caja")
                .OldAnnotation("Npgsql:Enum:unidad_venta", "peso,unidad")
                .OldAnnotation("Npgsql:PostgresExtension:citext", ",,");

            // gate §E (proposal.md:771-819): remitos — 18 columnas, PK, AK (habilita la FK
            // compuesta de items_remito y el soporte de FK 24 de movimientos_stock.id_remito, más
            // abajo), 5 FKs, 2 CHECKs, 5 índices nombrados a mano + el implícito de la AK.
            migrationBuilder.CreateTable(
                name: "remitos",
                columns: table => new
                {
                    id_remito = table.Column<int>(type: "integer", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    id_punto_venta = table.Column<int>(type: "integer", nullable: false),
                    id_cliente = table.Column<int>(type: "integer", nullable: false),
                    id_empleado = table.Column<int>(type: "integer", nullable: false),
                    numero = table.Column<long>(type: "bigint", nullable: true),
                    fecha_emision = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    fecha_salida = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    direccion_entrega = table.Column<string>(type: "text", nullable: true),
                    observaciones = table.Column<string>(type: "text", nullable: true),
                    subtotal = table.Column<decimal>(type: "numeric(14,2)", nullable: false),
                    descuento_total = table.Column<decimal>(type: "numeric(14,2)", nullable: false),
                    total = table.Column<decimal>(type: "numeric(14,2)", nullable: false),
                    estado = table.Column<EstadoRemito>(type: "estado_remito", nullable: false),
                    id_comprobante_venta = table.Column<int>(type: "integer", nullable: true),
                    created_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    updated_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    deleted_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    id_tenant = table.Column<int>(type: "integer", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_remitos", x => x.id_remito);
                    table.UniqueConstraint("ak_remitos_id_remito_id_tenant", x => new { x.id_remito, x.id_tenant });
                    table.CheckConstraint("ck_remitos_facturacion", "(id_comprobante_venta IS NULL) = (estado <> 'facturado')");
                    table.CheckConstraint("ck_remitos_salida_completa", "((numero IS NULL) = (fecha_salida IS NULL)) AND (estado IN ('borrador','anulado') OR numero IS NOT NULL)");
                    table.ForeignKey(
                        name: "fk_remitos_cliente",
                        columns: x => new { x.id_cliente, x.id_tenant },
                        principalTable: "clientes",
                        principalColumns: new[] { "id_cliente", "id_tenant" },
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "fk_remitos_comprobante_venta",
                        columns: x => new { x.id_comprobante_venta, x.id_tenant },
                        principalTable: "comprobantes_venta",
                        principalColumns: new[] { "id_comprobante_venta", "id_tenant" },
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "fk_remitos_empleado",
                        column: x => x.id_empleado,
                        principalTable: "usuarios",
                        principalColumn: "id_usuario",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "fk_remitos_punto_venta",
                        columns: x => new { x.id_punto_venta, x.id_tenant },
                        principalTable: "puntos_venta",
                        principalColumns: new[] { "id_punto_venta", "id_tenant" },
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "fk_remitos_tenant",
                        column: x => x.id_tenant,
                        principalTable: "tenants",
                        principalColumn: "id_tenant",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "items_remito",
                columns: table => new
                {
                    id_item = table.Column<int>(type: "integer", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    id_remito = table.Column<int>(type: "integer", nullable: false),
                    orden = table.Column<int>(type: "integer", nullable: false),
                    id_articulo = table.Column<int>(type: "integer", nullable: false),
                    descripcion = table.Column<string>(type: "text", nullable: false),
                    cantidad = table.Column<decimal>(type: "numeric(12,3)", nullable: false),
                    precio_unitario = table.Column<decimal>(type: "numeric(14,2)", nullable: false),
                    descuento = table.Column<decimal>(type: "numeric(14,2)", nullable: false, defaultValue: 0m),
                    total = table.Column<decimal>(type: "numeric(14,2)", nullable: false),
                    id_lista_precio = table.Column<int>(type: "integer", nullable: false),
                    id_oferta = table.Column<int>(type: "integer", nullable: true),
                    id_alicuota_iva = table.Column<int>(type: "integer", nullable: false),
                    porcentaje_iva = table.Column<decimal>(type: "numeric(5,2)", nullable: false),
                    costo_unitario = table.Column<decimal>(type: "numeric(14,2)", nullable: true),
                    costo_es_estimado = table.Column<bool>(type: "boolean", nullable: false, defaultValue: false),
                    id_lote = table.Column<int>(type: "integer", nullable: true),
                    created_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    updated_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    deleted_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    id_tenant = table.Column<int>(type: "integer", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_items_remito", x => x.id_item);
                    table.CheckConstraint("ck_items_remito_cantidad_positiva", "cantidad > 0");
                    table.CheckConstraint("ck_items_remito_costo_no_negativo", "costo_unitario IS NULL OR costo_unitario >= 0");
                    table.CheckConstraint("ck_items_remito_estimado_con_costo", "NOT costo_es_estimado OR costo_unitario IS NOT NULL");
                    table.ForeignKey(
                        name: "fk_items_remito_alicuota_iva",
                        column: x => x.id_alicuota_iva,
                        principalTable: "alicuotas_iva",
                        principalColumn: "id_alicuota_iva",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "fk_items_remito_articulo",
                        columns: x => new { x.id_articulo, x.id_tenant },
                        principalTable: "articulos",
                        principalColumns: new[] { "id_articulo", "id_tenant" },
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "fk_items_remito_lista_precio",
                        columns: x => new { x.id_lista_precio, x.id_tenant },
                        principalTable: "listas_precio",
                        principalColumns: new[] { "id_lista_precio", "id_tenant" },
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "fk_items_remito_lote",
                        columns: x => new { x.id_lote, x.id_articulo, x.id_tenant },
                        principalTable: "lotes",
                        principalColumns: new[] { "id_lote", "id_articulo", "id_tenant" },
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "fk_items_remito_oferta",
                        columns: x => new { x.id_oferta, x.id_tenant },
                        principalTable: "ofertas",
                        principalColumns: new[] { "id_oferta", "id_tenant" },
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "fk_items_remito_remito",
                        columns: x => new { x.id_remito, x.id_tenant },
                        principalTable: "remitos",
                        principalColumns: new[] { "id_remito", "id_tenant" },
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "fk_items_remito_tenant",
                        column: x => x.id_tenant,
                        principalTable: "tenants",
                        principalColumn: "id_tenant",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateIndex(
                name: "ix_remitos_tenant",
                table: "remitos",
                column: "id_tenant");

            migrationBuilder.CreateIndex(
                name: "ix_remitos_punto_venta_fecha",
                table: "remitos",
                columns: new[] { "id_punto_venta", "id_tenant", "fecha_emision" });

            migrationBuilder.CreateIndex(
                name: "ix_remitos_cliente",
                table: "remitos",
                columns: new[] { "id_cliente", "id_tenant" });

            migrationBuilder.CreateIndex(
                name: "ix_remitos_empleado",
                table: "remitos",
                column: "id_empleado");

            migrationBuilder.CreateIndex(
                name: "ix_remitos_comprobante_venta",
                table: "remitos",
                columns: new[] { "id_comprobante_venta", "id_tenant" });

            migrationBuilder.CreateIndex(
                name: "ux_remitos_numero",
                table: "remitos",
                columns: new[] { "id_tenant", "id_punto_venta", "numero" },
                unique: true,
                filter: "numero IS NOT NULL");

            // gate §F (proposal.md:826-881): items_remito — 20 columnas, PK, 7 FKs (la
            // compuesta contra la AK de arriba), 3 CHECKs, 7 índices nombrados a mano + la
            // unicidad de orden.
            migrationBuilder.CreateIndex(
                name: "ix_items_remito_tenant",
                table: "items_remito",
                column: "id_tenant");

            migrationBuilder.CreateIndex(
                name: "ix_items_remito_remito",
                table: "items_remito",
                columns: new[] { "id_remito", "id_tenant" });

            migrationBuilder.CreateIndex(
                name: "ix_items_remito_articulo",
                table: "items_remito",
                columns: new[] { "id_articulo", "id_tenant" });

            migrationBuilder.CreateIndex(
                name: "ix_items_remito_lista_precio",
                table: "items_remito",
                columns: new[] { "id_lista_precio", "id_tenant" });

            migrationBuilder.CreateIndex(
                name: "ix_items_remito_oferta",
                table: "items_remito",
                columns: new[] { "id_oferta", "id_tenant" });

            migrationBuilder.CreateIndex(
                name: "ix_items_remito_alicuota_iva",
                table: "items_remito",
                column: "id_alicuota_iva");

            migrationBuilder.CreateIndex(
                name: "ix_items_remito_lote",
                table: "items_remito",
                columns: new[] { "id_lote", "id_articulo", "id_tenant" });

            migrationBuilder.CreateIndex(
                name: "ux_items_remito_orden",
                table: "items_remito",
                columns: new[] { "id_remito", "orden" },
                unique: true);

            // gate §H (proposal.md:914-938): ALTER aditivo sobre movimientos_stock — el
            // documento del cuarto write site. Metadata-only en PG 11+ (columna nullable sin
            // default), FK compuesta MATCH SIMPLE e índice de soporte declarado a mano. DESPUÉS
            // de las dos CREATE TABLE de arriba (orden topológico: la FK necesita que remitos y
            // su AK ya existan).
            migrationBuilder.AddColumn<int>(
                name: "id_remito",
                table: "movimientos_stock",
                type: "integer",
                nullable: true);

            migrationBuilder.AddForeignKey(
                name: "fk_movimientos_stock_remito",
                table: "movimientos_stock",
                columns: new[] { "id_remito", "id_tenant" },
                principalTable: "remitos",
                principalColumns: new[] { "id_remito", "id_tenant" },
                onDelete: ReferentialAction.Restrict);

            migrationBuilder.CreateIndex(
                name: "ix_movimientos_stock_remito",
                table: "movimientos_stock",
                columns: new[] { "id_remito", "id_tenant" });

            // gate §I, data statement 2 (proposal.md:946-950) — TXR para bases YA MIGRADAS,
            // mismo guard EXISTS/NOT EXISTS que RC (CuentaCorrienteEtapa7)/C-*
            // (ComprasYTransferenciasEtapa8): el seeder de InicializadorDeBaseDeDatos.
            // TiposComprobanteBase (task 4.20) siembra TXR para una base FRESCA, este statement
            // cierra el mismo hueco en una base que YA tenía tipos_comprobante poblado antes de
            // esta etapa.
            migrationBuilder.Sql(
                """
                INSERT INTO tipos_comprobante (clase, codigo, nombre, letra, signo, discrimina_iva, es_fiscal, afecta_stock, activo, created_at, updated_at)
                SELECT 'venta', 'TXR', 'Ticket X por remitos', 'X', 1, false, false, false, true, now(), now()
                WHERE EXISTS (SELECT 1 FROM tipos_comprobante)
                  AND NOT EXISTS (SELECT 1 FROM tipos_comprobante WHERE codigo = 'TXR');
                """);

            // RLS al final, en las DOS tablas nuevas (ADR-15 / la convención de las etapas
            // 12/14/15/16/17-slice-1): la conexión de migración no tiene app_tenant_actual()
            // seteado y el data statement de arriba no depende de RLS estar activo todavía.
            migrationBuilder.HabilitarRlsDeTenant("remitos");
            migrationBuilder.HabilitarRlsDeTenant("items_remito");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropForeignKey(
                name: "fk_movimientos_stock_remito",
                table: "movimientos_stock");

            migrationBuilder.DropIndex(
                name: "ix_movimientos_stock_remito",
                table: "movimientos_stock");

            migrationBuilder.DropColumn(
                name: "id_remito",
                table: "movimientos_stock");

            migrationBuilder.DropTable(
                name: "items_remito");

            migrationBuilder.DropTable(
                name: "remitos");

            // Sin reversa de `motivo_stock` (proposal §B, gate §B): Postgres no soporta DROP
            // VALUE, así que 'remito' queda como miembro muerto documentado tras el rollback —
            // el resto de la reversa (FKs, tablas, índices, CHECKs, columna, tipo estado_remito)
            // sí se ejecuta. Mismo criterio que LotesYVencimientosEtapa12 con
            // decomiso/reclasificacion.
            migrationBuilder.AlterDatabase()
                .Annotation("Npgsql:Enum:categoria_gasto", "impuestos,otros,proveedor,servicios,sueldos,viaticos")
                .Annotation("Npgsql:Enum:clase_comprobante", "compra,venta")
                .Annotation("Npgsql:Enum:comportamiento_medio_pago", "cuenta_corriente,efectivo,electronico")
                .Annotation("Npgsql:Enum:estado_compra", "anulada,borrador,confirmada")
                .Annotation("Npgsql:Enum:estado_comprobante", "anulado,emitido")
                .Annotation("Npgsql:Enum:estado_orden_compra", "anulada,borrador,cerrada,enviada,recibida_parcial")
                .Annotation("Npgsql:Enum:estado_presupuesto", "anulado,borrador,convertido,enviado")
                .Annotation("Npgsql:Enum:estado_tenant", "activo,baja,suspendido")
                .Annotation("Npgsql:Enum:estado_turno", "abierto,cerrado")
                .Annotation("Npgsql:Enum:estado_usuario", "activo,bloqueado,inactivo")
                .Annotation("Npgsql:Enum:modo_lista", "derivada,fija")
                .Annotation("Npgsql:Enum:motivo_stock", "ajuste,anulacion,compra,decomiso,inventario,reclasificacion,transferencia,venta")
                .Annotation("Npgsql:Enum:tipo_documento", "cuil,cuit,dni,otro,pasaporte")
                .Annotation("Npgsql:Enum:tipo_movimiento_caja", "apertura_cajon,refuerzo,retiro")
                .Annotation("Npgsql:Enum:tipo_movimiento_cc", "actualizacion_precios,ajuste,consumo,pago")
                .Annotation("Npgsql:Enum:tipo_movimiento_cc_proveedor", "ajuste,apertura,compra,pago")
                .Annotation("Npgsql:Enum:tipo_movimiento_tesoreria", "ajuste,deposito,gasto,retiro_caja")
                .Annotation("Npgsql:Enum:unidad_venta", "peso,unidad")
                .Annotation("Npgsql:PostgresExtension:citext", ",,")
                .OldAnnotation("Npgsql:Enum:categoria_gasto", "impuestos,otros,proveedor,servicios,sueldos,viaticos")
                .OldAnnotation("Npgsql:Enum:clase_comprobante", "compra,venta")
                .OldAnnotation("Npgsql:Enum:comportamiento_medio_pago", "cuenta_corriente,efectivo,electronico")
                .OldAnnotation("Npgsql:Enum:estado_compra", "anulada,borrador,confirmada")
                .OldAnnotation("Npgsql:Enum:estado_comprobante", "anulado,emitido")
                .OldAnnotation("Npgsql:Enum:estado_orden_compra", "anulada,borrador,cerrada,enviada,recibida_parcial")
                .OldAnnotation("Npgsql:Enum:estado_presupuesto", "anulado,borrador,convertido,enviado")
                .OldAnnotation("Npgsql:Enum:estado_remito", "borrador,emitido,facturado,anulado")
                .OldAnnotation("Npgsql:Enum:estado_tenant", "activo,baja,suspendido")
                .OldAnnotation("Npgsql:Enum:estado_turno", "abierto,cerrado")
                .OldAnnotation("Npgsql:Enum:estado_usuario", "activo,bloqueado,inactivo")
                .OldAnnotation("Npgsql:Enum:modo_lista", "derivada,fija")
                .OldAnnotation("Npgsql:Enum:motivo_stock", "ajuste,anulacion,compra,decomiso,inventario,reclasificacion,remito,transferencia,venta")
                .OldAnnotation("Npgsql:Enum:tipo_documento", "cuil,cuit,dni,otro,pasaporte")
                .OldAnnotation("Npgsql:Enum:tipo_movimiento_caja", "apertura_cajon,refuerzo,retiro")
                .OldAnnotation("Npgsql:Enum:tipo_movimiento_cc", "actualizacion_precios,ajuste,consumo,pago")
                .OldAnnotation("Npgsql:Enum:tipo_movimiento_cc_proveedor", "ajuste,apertura,compra,pago")
                .OldAnnotation("Npgsql:Enum:tipo_movimiento_tesoreria", "ajuste,deposito,gasto,retiro_caja")
                .OldAnnotation("Npgsql:Enum:unidad_venta", "peso,unidad")
                .OldAnnotation("Npgsql:PostgresExtension:citext", ",,");

            // gate §I, data statement 3 (proposal.md:952-954) — desactiva en vez de borrar, para
            // que un TXR ya emitido siga siendo legible después de un rollback (mismo criterio
            // que CuentaCorrienteEtapa7/ComprasYTransferenciasEtapa8 con RC/C-*). Último paso del
            // Down (proposal.md:1092-1095, rollback plan), después de revertir todo lo demás.
            migrationBuilder.Sql("UPDATE tipos_comprobante SET activo = false WHERE codigo = 'TXR';");
        }
    }
}
