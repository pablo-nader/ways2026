using System;
using Microsoft.EntityFrameworkCore.Migrations;
using Npgsql.EntityFrameworkCore.PostgreSQL.Metadata;
using Ways.Domain.Compras;
using Ways.Infrastructure.Multitenancy;

#nullable disable

namespace Ways.Infrastructure.Persistencia.Migraciones
{
    /// <inheritdoc />
    public partial class OrdenesDeCompraEtapa16 : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            // gate §A (proposal.md:512-522): CREATE TYPE estado_orden_compra AS ENUM
            // ('borrador', 'enviada', 'recibida_parcial', 'cerrada', 'anulada') — orden = orden
            // de miembros del enum C# (npgsql.MapEnum<T>()). `dotnet ef migrations add` serializa
            // esta anotación en orden ALFABÉTICO por defecto (mismo residuo ya documentado en
            // WaysDbContext.cs:183-186 y en la migración de la etapa 15 para
            // tipo_movimiento_cc_proveedor/estado_usuario/estado_tenant) — se corrige a mano acá
            // para que el CREATE TYPE resultante sea VERBATIM al gate.
            migrationBuilder.AlterDatabase()
                .Annotation("Npgsql:Enum:categoria_gasto", "impuestos,otros,proveedor,servicios,sueldos,viaticos")
                .Annotation("Npgsql:Enum:clase_comprobante", "compra,venta")
                .Annotation("Npgsql:Enum:comportamiento_medio_pago", "cuenta_corriente,efectivo,electronico")
                .Annotation("Npgsql:Enum:estado_compra", "anulada,borrador,confirmada")
                .Annotation("Npgsql:Enum:estado_comprobante", "anulado,emitido")
                .Annotation("Npgsql:Enum:estado_orden_compra", "borrador,enviada,recibida_parcial,cerrada,anulada")
                .Annotation("Npgsql:Enum:estado_tenant", "activo,baja,suspendido")
                .Annotation("Npgsql:Enum:estado_turno", "abierto,cerrado")
                .Annotation("Npgsql:Enum:estado_usuario", "activo,bloqueado,inactivo")
                .Annotation("Npgsql:Enum:modo_lista", "derivada,fija")
                .Annotation("Npgsql:Enum:motivo_stock", "ajuste,anulacion,compra,decomiso,inventario,reclasificacion,transferencia,venta")
                .Annotation("Npgsql:Enum:tipo_documento", "cuil,cuit,dni,otro,pasaporte")
                .Annotation("Npgsql:Enum:tipo_movimiento_caja", "apertura_cajon,refuerzo,retiro")
                .Annotation("Npgsql:Enum:tipo_movimiento_cc", "actualizacion_precios,ajuste,consumo,pago")
                .Annotation("Npgsql:Enum:tipo_movimiento_cc_proveedor", "apertura,compra,pago,ajuste")
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
                .OldAnnotation("Npgsql:Enum:tipo_movimiento_cc_proveedor", "apertura,compra,pago,ajuste")
                .OldAnnotation("Npgsql:Enum:tipo_movimiento_tesoreria", "ajuste,deposito,gasto,retiro_caja")
                .OldAnnotation("Npgsql:Enum:unidad_venta", "peso,unidad")
                .OldAnnotation("Npgsql:PostgresExtension:citext", ",,");

            // gate §B (proposal.md:540-597): ordenes_compra — 16 columnas, PK, AK (habilita las
            // FKs compuestas de items_orden_compra y de la ALTER de comprobantes_compra, más
            // abajo), 5 FKs, 2 CHECKs, 6 índices nombrados a mano + el implícito de la AK.
            migrationBuilder.CreateTable(
                name: "ordenes_compra",
                columns: table => new
                {
                    id_orden_compra = table.Column<int>(type: "integer", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    id_tenant = table.Column<int>(type: "integer", nullable: false),
                    id_punto_venta = table.Column<int>(type: "integer", nullable: false),
                    id_proveedor = table.Column<int>(type: "integer", nullable: false),
                    id_empleado = table.Column<int>(type: "integer", nullable: false),
                    numero = table.Column<long>(type: "bigint", nullable: true),
                    fecha_emision = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    fecha_envio = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    fecha_esperada = table.Column<DateOnly>(type: "date", nullable: true),
                    fecha_cierre = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    id_empleado_cierre = table.Column<int>(type: "integer", nullable: true),
                    observaciones = table.Column<string>(type: "text", nullable: true),
                    estado = table.Column<EstadoOrdenCompra>(type: "estado_orden_compra", nullable: false),
                    created_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    updated_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    deleted_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_ordenes_compra", x => x.id_orden_compra);
                    table.UniqueConstraint("ak_ordenes_compra_id_orden_compra_id_tenant", x => new { x.id_orden_compra, x.id_tenant });
                    table.CheckConstraint("ck_ordenes_compra_cierre", "((fecha_cierre IS NULL) = (estado <> 'cerrada')) AND (id_empleado_cierre IS NULL OR fecha_cierre IS NOT NULL)");
                    table.CheckConstraint("ck_ordenes_compra_envio_completo", "((numero IS NULL) = (fecha_envio IS NULL)) AND (estado IN ('borrador','anulada') OR numero IS NOT NULL)");
                    table.ForeignKey(
                        name: "fk_ordenes_compra_empleado",
                        column: x => x.id_empleado,
                        principalTable: "usuarios",
                        principalColumn: "id_usuario",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "fk_ordenes_compra_empleado_cierre",
                        column: x => x.id_empleado_cierre,
                        principalTable: "usuarios",
                        principalColumn: "id_usuario",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "fk_ordenes_compra_proveedor",
                        columns: x => new { x.id_proveedor, x.id_tenant },
                        principalTable: "proveedores",
                        principalColumns: new[] { "id_proveedor", "id_tenant" },
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "fk_ordenes_compra_punto_venta",
                        columns: x => new { x.id_punto_venta, x.id_tenant },
                        principalTable: "puntos_venta",
                        principalColumns: new[] { "id_punto_venta", "id_tenant" },
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "fk_ordenes_compra_tenant",
                        column: x => x.id_tenant,
                        principalTable: "tenants",
                        principalColumn: "id_tenant",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateIndex(
                name: "ix_ordenes_compra_tenant",
                table: "ordenes_compra",
                column: "id_tenant");

            migrationBuilder.CreateIndex(
                name: "ix_ordenes_compra_punto_venta_fecha",
                table: "ordenes_compra",
                columns: new[] { "id_punto_venta", "id_tenant", "fecha_emision" });

            migrationBuilder.CreateIndex(
                name: "ix_ordenes_compra_proveedor",
                table: "ordenes_compra",
                columns: new[] { "id_proveedor", "id_tenant" });

            migrationBuilder.CreateIndex(
                name: "ix_ordenes_compra_empleado",
                table: "ordenes_compra",
                column: "id_empleado");

            migrationBuilder.CreateIndex(
                name: "ix_ordenes_compra_empleado_cierre",
                table: "ordenes_compra",
                column: "id_empleado_cierre");

            migrationBuilder.CreateIndex(
                name: "ux_ordenes_compra_numero",
                table: "ordenes_compra",
                columns: new[] { "id_tenant", "id_punto_venta", "numero" },
                unique: true,
                filter: "numero IS NOT NULL");

            // gate §C (proposal.md:606-644): items_orden_compra — 11 columnas, PK, 3 FKs (la
            // compuesta contra la AK de arriba), 2 CHECKs, 3 índices nombrados a mano + la
            // unicidad de orden.
            migrationBuilder.CreateTable(
                name: "items_orden_compra",
                columns: table => new
                {
                    id_item = table.Column<int>(type: "integer", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    id_tenant = table.Column<int>(type: "integer", nullable: false),
                    id_orden_compra = table.Column<int>(type: "integer", nullable: false),
                    orden = table.Column<int>(type: "integer", nullable: false),
                    id_articulo = table.Column<int>(type: "integer", nullable: false),
                    descripcion = table.Column<string>(type: "text", nullable: false),
                    cantidad_pedida = table.Column<decimal>(type: "numeric(12,3)", nullable: false),
                    costo_unitario_estimado = table.Column<decimal>(type: "numeric(14,4)", nullable: true),
                    created_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    updated_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    deleted_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_items_orden_compra", x => x.id_item);
                    table.CheckConstraint("ck_items_orden_compra_cantidad_positiva", "cantidad_pedida > 0");
                    table.CheckConstraint("ck_items_orden_compra_costo_no_negativo", "costo_unitario_estimado IS NULL OR costo_unitario_estimado >= 0");
                    table.ForeignKey(
                        name: "fk_items_orden_compra_articulo",
                        columns: x => new { x.id_articulo, x.id_tenant },
                        principalTable: "articulos",
                        principalColumns: new[] { "id_articulo", "id_tenant" },
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "fk_items_orden_compra_orden_compra",
                        columns: x => new { x.id_orden_compra, x.id_tenant },
                        principalTable: "ordenes_compra",
                        principalColumns: new[] { "id_orden_compra", "id_tenant" },
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "fk_items_orden_compra_tenant",
                        column: x => x.id_tenant,
                        principalTable: "tenants",
                        principalColumn: "id_tenant",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateIndex(
                name: "ix_items_orden_compra_tenant",
                table: "items_orden_compra",
                column: "id_tenant");

            migrationBuilder.CreateIndex(
                name: "ix_items_orden_compra_orden_compra",
                table: "items_orden_compra",
                columns: new[] { "id_orden_compra", "id_tenant" });

            migrationBuilder.CreateIndex(
                name: "ix_items_orden_compra_articulo",
                table: "items_orden_compra",
                columns: new[] { "id_articulo", "id_tenant" });

            migrationBuilder.CreateIndex(
                name: "ux_items_orden_compra_orden",
                table: "items_orden_compra",
                columns: new[] { "id_orden_compra", "orden" },
                unique: true);

            // gate §D (proposal.md:649-663): ALTER aditivo sobre comprobantes_compra — el link.
            // Metadata-only en PG 11+ (columna nullable sin default), FK compuesta MATCH SIMPLE
            // (el default: con id_orden_compra NULL la constraint no se chequea) e índice de
            // soporte declarado a mano (nunca el IX_... autogenerado). DESPUÉS de las dos CREATE
            // TABLE de arriba (proposal.md:721-724, orden topológico: la FK necesita que
            // ordenes_compra y su AK ya existan).
            migrationBuilder.AddColumn<int>(
                name: "id_orden_compra",
                table: "comprobantes_compra",
                type: "integer",
                nullable: true);

            migrationBuilder.AddForeignKey(
                name: "fk_comprobantes_compra_orden_compra",
                table: "comprobantes_compra",
                columns: new[] { "id_orden_compra", "id_tenant" },
                principalTable: "ordenes_compra",
                principalColumns: new[] { "id_orden_compra", "id_tenant" },
                onDelete: ReferentialAction.Restrict);

            migrationBuilder.CreateIndex(
                name: "ix_comprobantes_compra_orden_compra",
                table: "comprobantes_compra",
                columns: new[] { "id_orden_compra", "id_tenant" });

            // RLS al final, en las DOS tablas nuevas (proposal.md:721-724, ADR-15 / la convención
            // de las etapas 14/15): la conexión de migración no tiene app_tenant_actual()
            // seteado y esta migración no lleva ningún data statement cuya corrección pudiera
            // depender de activar RLS antes.
            migrationBuilder.HabilitarRlsDeTenant("ordenes_compra");
            migrationBuilder.HabilitarRlsDeTenant("items_orden_compra");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropForeignKey(
                name: "fk_comprobantes_compra_orden_compra",
                table: "comprobantes_compra");

            migrationBuilder.DropIndex(
                name: "ix_comprobantes_compra_orden_compra",
                table: "comprobantes_compra");

            migrationBuilder.DropColumn(
                name: "id_orden_compra",
                table: "comprobantes_compra");

            migrationBuilder.DropTable(
                name: "items_orden_compra");

            migrationBuilder.DropTable(
                name: "ordenes_compra");

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
                .Annotation("Npgsql:Enum:tipo_movimiento_cc_proveedor", "apertura,compra,pago,ajuste")
                .Annotation("Npgsql:Enum:tipo_movimiento_tesoreria", "ajuste,deposito,gasto,retiro_caja")
                .Annotation("Npgsql:Enum:unidad_venta", "peso,unidad")
                .Annotation("Npgsql:PostgresExtension:citext", ",,")
                .OldAnnotation("Npgsql:Enum:categoria_gasto", "impuestos,otros,proveedor,servicios,sueldos,viaticos")
                .OldAnnotation("Npgsql:Enum:clase_comprobante", "compra,venta")
                .OldAnnotation("Npgsql:Enum:comportamiento_medio_pago", "cuenta_corriente,efectivo,electronico")
                .OldAnnotation("Npgsql:Enum:estado_compra", "anulada,borrador,confirmada")
                .OldAnnotation("Npgsql:Enum:estado_comprobante", "anulado,emitido")
                .OldAnnotation("Npgsql:Enum:estado_orden_compra", "borrador,enviada,recibida_parcial,cerrada,anulada")
                .OldAnnotation("Npgsql:Enum:estado_tenant", "activo,baja,suspendido")
                .OldAnnotation("Npgsql:Enum:estado_turno", "abierto,cerrado")
                .OldAnnotation("Npgsql:Enum:estado_usuario", "activo,bloqueado,inactivo")
                .OldAnnotation("Npgsql:Enum:modo_lista", "derivada,fija")
                .OldAnnotation("Npgsql:Enum:motivo_stock", "ajuste,anulacion,compra,decomiso,inventario,reclasificacion,transferencia,venta")
                .OldAnnotation("Npgsql:Enum:tipo_documento", "cuil,cuit,dni,otro,pasaporte")
                .OldAnnotation("Npgsql:Enum:tipo_movimiento_caja", "apertura_cajon,refuerzo,retiro")
                .OldAnnotation("Npgsql:Enum:tipo_movimiento_cc", "actualizacion_precios,ajuste,consumo,pago")
                .OldAnnotation("Npgsql:Enum:tipo_movimiento_cc_proveedor", "apertura,compra,pago,ajuste")
                .OldAnnotation("Npgsql:Enum:tipo_movimiento_tesoreria", "ajuste,deposito,gasto,retiro_caja")
                .OldAnnotation("Npgsql:Enum:unidad_venta", "peso,unidad")
                .OldAnnotation("Npgsql:PostgresExtension:citext", ",,");
        }
    }
}
