using System;
using Microsoft.EntityFrameworkCore.Migrations;
using Npgsql.EntityFrameworkCore.PostgreSQL.Metadata;
using Ways.Domain.Ventas;
using Ways.Infrastructure.Multitenancy;

#nullable disable

namespace Ways.Infrastructure.Persistencia.Migraciones
{
    /// <inheritdoc />
    public partial class PresupuestosEtapa17 : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            // gate §A (proposal.md:624-625): CREATE TYPE estado_presupuesto AS ENUM
            // ('borrador', 'enviado', 'convertido', 'anulado') — orden = orden de miembros del
            // enum C# (npgsql.MapEnum<T>()). `dotnet ef migrations add` serializa esta
            // anotación en orden ALFABÉTICO por defecto (mismo residuo ya documentado en
            // WaysDbContext.cs:197-200 y en las migraciones de las etapas 15/16) — se corrige a
            // mano acá para que el CREATE TYPE resultante sea VERBATIM al gate (task 1.11,
            // registrado como desvío esperado, no un hallazgo).
            migrationBuilder.AlterDatabase()
                .Annotation("Npgsql:Enum:categoria_gasto", "impuestos,otros,proveedor,servicios,sueldos,viaticos")
                .Annotation("Npgsql:Enum:clase_comprobante", "compra,venta")
                .Annotation("Npgsql:Enum:comportamiento_medio_pago", "cuenta_corriente,efectivo,electronico")
                .Annotation("Npgsql:Enum:estado_compra", "anulada,borrador,confirmada")
                .Annotation("Npgsql:Enum:estado_comprobante", "anulado,emitido")
                .Annotation("Npgsql:Enum:estado_orden_compra", "borrador,enviada,recibida_parcial,cerrada,anulada")
                .Annotation("Npgsql:Enum:estado_presupuesto", "borrador,enviado,convertido,anulado")
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

            // gate §C (proposal.md:653-702): presupuestos — 17 columnas, PK, AK (habilita la FK
            // compuesta de items_presupuesto y la ALTER de comprobantes_venta, más abajo), 4
            // FKs, 1 CHECK, 5 índices nombrados a mano + el implícito de la AK.
            migrationBuilder.CreateTable(
                name: "presupuestos",
                columns: table => new
                {
                    id_presupuesto = table.Column<int>(type: "integer", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    id_tenant = table.Column<int>(type: "integer", nullable: false),
                    id_punto_venta = table.Column<int>(type: "integer", nullable: false),
                    id_cliente = table.Column<int>(type: "integer", nullable: false),
                    id_empleado = table.Column<int>(type: "integer", nullable: false),
                    numero = table.Column<long>(type: "bigint", nullable: true),
                    fecha_emision = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    fecha_envio = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    vencimiento = table.Column<DateOnly>(type: "date", nullable: true),
                    observaciones = table.Column<string>(type: "text", nullable: true),
                    subtotal = table.Column<decimal>(type: "numeric(14,2)", nullable: false),
                    descuento_total = table.Column<decimal>(type: "numeric(14,2)", nullable: false),
                    total = table.Column<decimal>(type: "numeric(14,2)", nullable: false),
                    estado = table.Column<EstadoPresupuesto>(type: "estado_presupuesto", nullable: false),
                    created_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    updated_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    deleted_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_presupuestos", x => x.id_presupuesto);
                    table.UniqueConstraint("ak_presupuestos_id_presupuesto_id_tenant", x => new { x.id_presupuesto, x.id_tenant });
                    table.CheckConstraint("ck_presupuestos_envio_completo", "((numero IS NULL) = (fecha_envio IS NULL)) AND ((numero IS NULL) = (vencimiento IS NULL)) AND (estado IN ('borrador','anulado') OR numero IS NOT NULL)");
                    table.ForeignKey(
                        name: "fk_presupuestos_cliente",
                        columns: x => new { x.id_cliente, x.id_tenant },
                        principalTable: "clientes",
                        principalColumns: new[] { "id_cliente", "id_tenant" },
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "fk_presupuestos_empleado",
                        column: x => x.id_empleado,
                        principalTable: "usuarios",
                        principalColumn: "id_usuario",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "fk_presupuestos_punto_venta",
                        columns: x => new { x.id_punto_venta, x.id_tenant },
                        principalTable: "puntos_venta",
                        principalColumns: new[] { "id_punto_venta", "id_tenant" },
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "fk_presupuestos_tenant",
                        column: x => x.id_tenant,
                        principalTable: "tenants",
                        principalColumn: "id_tenant",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateIndex(
                name: "ix_presupuestos_tenant",
                table: "presupuestos",
                column: "id_tenant");

            migrationBuilder.CreateIndex(
                name: "ix_presupuestos_punto_venta_fecha",
                table: "presupuestos",
                columns: new[] { "id_punto_venta", "id_tenant", "fecha_emision" });

            migrationBuilder.CreateIndex(
                name: "ix_presupuestos_cliente",
                table: "presupuestos",
                columns: new[] { "id_cliente", "id_tenant" });

            migrationBuilder.CreateIndex(
                name: "ix_presupuestos_empleado",
                table: "presupuestos",
                column: "id_empleado");

            migrationBuilder.CreateIndex(
                name: "ux_presupuestos_numero",
                table: "presupuestos",
                columns: new[] { "id_tenant", "id_punto_venta", "numero" },
                unique: true,
                filter: "numero IS NOT NULL");

            // gate §D (proposal.md:714-763): items_presupuesto — 17 columnas, PK, 6 FKs (la
            // compuesta contra la AK de arriba), 1 CHECK, 6 índices nombrados a mano + la
            // unicidad de orden.
            migrationBuilder.CreateTable(
                name: "items_presupuesto",
                columns: table => new
                {
                    id_item = table.Column<int>(type: "integer", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    id_tenant = table.Column<int>(type: "integer", nullable: false),
                    id_presupuesto = table.Column<int>(type: "integer", nullable: false),
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
                    created_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    updated_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    deleted_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_items_presupuesto", x => x.id_item);
                    table.CheckConstraint("ck_items_presupuesto_cantidad_positiva", "cantidad > 0");
                    table.ForeignKey(
                        name: "fk_items_presupuesto_alicuota_iva",
                        column: x => x.id_alicuota_iva,
                        principalTable: "alicuotas_iva",
                        principalColumn: "id_alicuota_iva",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "fk_items_presupuesto_articulo",
                        columns: x => new { x.id_articulo, x.id_tenant },
                        principalTable: "articulos",
                        principalColumns: new[] { "id_articulo", "id_tenant" },
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "fk_items_presupuesto_lista_precio",
                        columns: x => new { x.id_lista_precio, x.id_tenant },
                        principalTable: "listas_precio",
                        principalColumns: new[] { "id_lista_precio", "id_tenant" },
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "fk_items_presupuesto_oferta",
                        columns: x => new { x.id_oferta, x.id_tenant },
                        principalTable: "ofertas",
                        principalColumns: new[] { "id_oferta", "id_tenant" },
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "fk_items_presupuesto_presupuesto",
                        columns: x => new { x.id_presupuesto, x.id_tenant },
                        principalTable: "presupuestos",
                        principalColumns: new[] { "id_presupuesto", "id_tenant" },
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "fk_items_presupuesto_tenant",
                        column: x => x.id_tenant,
                        principalTable: "tenants",
                        principalColumn: "id_tenant",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateIndex(
                name: "ix_items_presupuesto_tenant",
                table: "items_presupuesto",
                column: "id_tenant");

            migrationBuilder.CreateIndex(
                name: "ix_items_presupuesto_presupuesto",
                table: "items_presupuesto",
                columns: new[] { "id_presupuesto", "id_tenant" });

            migrationBuilder.CreateIndex(
                name: "ix_items_presupuesto_articulo",
                table: "items_presupuesto",
                columns: new[] { "id_articulo", "id_tenant" });

            migrationBuilder.CreateIndex(
                name: "ix_items_presupuesto_lista_precio",
                table: "items_presupuesto",
                columns: new[] { "id_lista_precio", "id_tenant" });

            migrationBuilder.CreateIndex(
                name: "ix_items_presupuesto_oferta",
                table: "items_presupuesto",
                columns: new[] { "id_oferta", "id_tenant" });

            migrationBuilder.CreateIndex(
                name: "ix_items_presupuesto_alicuota_iva",
                table: "items_presupuesto",
                column: "id_alicuota_iva");

            migrationBuilder.CreateIndex(
                name: "ux_items_presupuesto_orden",
                table: "items_presupuesto",
                columns: new[] { "id_presupuesto", "orden" },
                unique: true);

            // gate §G (proposal.md:887-903): ALTER aditivo sobre comprobantes_venta — el link de
            // conversión. Metadata-only en PG 11+ (columna nullable sin default), FK compuesta
            // MATCH SIMPLE (el default: con id_presupuesto_origen NULL la constraint no se
            // chequea) e índice UNIQUE PARCIAL de soporte declarado a mano (nunca el IX_...
            // autogenerado) — la garantía 1:1 de conversión Y el soporte de FK a la vez. DESPUÉS
            // de las dos CREATE TABLE de arriba (orden topológico: la FK necesita que
            // presupuestos y su AK ya existan — lección 42830).
            migrationBuilder.AddColumn<int>(
                name: "id_presupuesto_origen",
                table: "comprobantes_venta",
                type: "integer",
                nullable: true);

            migrationBuilder.AddForeignKey(
                name: "fk_comprobantes_venta_presupuesto_origen",
                table: "comprobantes_venta",
                columns: new[] { "id_presupuesto_origen", "id_tenant" },
                principalTable: "presupuestos",
                principalColumns: new[] { "id_presupuesto", "id_tenant" },
                onDelete: ReferentialAction.Restrict);

            migrationBuilder.CreateIndex(
                name: "ux_comprobantes_venta_presupuesto_origen",
                table: "comprobantes_venta",
                columns: new[] { "id_presupuesto_origen", "id_tenant" },
                unique: true,
                filter: "id_presupuesto_origen IS NOT NULL");

            // gate §I, data statement 1 (proposal.md:943-944) — net 1 de la decisión 2 (el
            // cierre del PRE latente, explore.md). Idempotente por construcción (un UPDATE
            // repetido no cambia nada) — a diferencia de los INSERT guardados de RC/C-*/TXR,
            // este statement no necesita EXISTS/NOT EXISTS: apaga una fila que YA EXISTE en toda
            // base migrada desde antes de esta etapa. Net 1b (el seed change de
            // InicializadorDeBaseDeDatos.TiposComprobanteBase) es quien cierra el mismo hueco en
            // una base FRESCA — este statement por sí solo no alcanza ahí porque el seeder corre
            // después de las migraciones, contra una tabla vacía (:432). Los dos nets se prueban
            // de forma INDEPENDIENTE (mutation targets 10/11, tasks 1.38/1.39).
            migrationBuilder.Sql("UPDATE tipos_comprobante SET activo = false WHERE codigo = 'PRE';");

            // RLS al final, en las DOS tablas nuevas (ADR-15 / la convención de las etapas
            // 14/15/16): la conexión de migración no tiene app_tenant_actual() seteado y el
            // único data statement de arriba no depende de RLS estar activo todavía.
            migrationBuilder.HabilitarRlsDeTenant("presupuestos");
            migrationBuilder.HabilitarRlsDeTenant("items_presupuesto");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            // proposal.md:1085-1090: reactivar PRE (`activo = true`) SOLO si el resolver guard de
            // la slice 3 (ServicioDeVentas.cs:930, `|| !tipo.AfectaStock`) también se revierte —
            // de lo contrario dejar PRE inactivo es el residuo más seguro. Este Down() (slice 1
            // aislada) NO sabe si esa cláusula sigue presente, así que NO reactiva PRE — el
            // Down explícitamente registrado por el gate, no una omisión.
            migrationBuilder.DropIndex(
                name: "ux_comprobantes_venta_presupuesto_origen",
                table: "comprobantes_venta");

            migrationBuilder.DropForeignKey(
                name: "fk_comprobantes_venta_presupuesto_origen",
                table: "comprobantes_venta");

            migrationBuilder.DropColumn(
                name: "id_presupuesto_origen",
                table: "comprobantes_venta");

            migrationBuilder.DropTable(
                name: "items_presupuesto");

            migrationBuilder.DropTable(
                name: "presupuestos");

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
                .OldAnnotation("Npgsql:Enum:estado_orden_compra", "borrador,enviada,recibida_parcial,cerrada,anulada")
                .OldAnnotation("Npgsql:Enum:estado_presupuesto", "borrador,enviado,convertido,anulado")
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
