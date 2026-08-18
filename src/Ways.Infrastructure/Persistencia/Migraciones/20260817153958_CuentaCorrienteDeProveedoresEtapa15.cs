using System;
using Microsoft.EntityFrameworkCore.Migrations;
using Npgsql.EntityFrameworkCore.PostgreSQL.Metadata;
using Ways.Domain.CuentaCorriente;
using Ways.Infrastructure.Multitenancy;

#nullable disable

namespace Ways.Infrastructure.Persistencia.Migraciones
{
    /// <inheritdoc />
    public partial class CuentaCorrienteDeProveedoresEtapa15 : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            // gate §A (proposal.md:499-508): CREATE TYPE tipo_movimiento_cc_proveedor AS ENUM
            // ('apertura', 'compra', 'pago', 'ajuste') — orden = orden de miembros del enum C#
            // (npgsql.MapEnum<T>()). `dotnet ef migrations add` serializa esta anotación en
            // orden ALFABÉTICO por defecto (mismo comportamiento ya documentado en
            // WaysDbContext.cs:183-186 para tipo_movimiento_cc/estado_usuario/estado_tenant) —
            // se corrige a mano para que el CREATE TYPE resultante sea VERBATIM al gate.
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

            // gate §D (proposal.md:670-684): ALTER TABLE gastos — la clave alterna que requiere
            // la FK 6 de la tabla nueva. DESVIACIÓN REGISTRADA vs. la nota de la tarea 1.10 del
            // orquestador ("... ALTER proveedores + statement 2 → ALTER gastos → RLS al
            // final"): esa posición es SQL-inejecutable — Postgres exige que la unique
            // constraint referenciada por una FK compuesta exista ANTES de crear esa FK, y
            // fk_movimientos_cuenta_corriente_proveedor_gasto se declara dentro del mismo
            // CreateTable de abajo. La propia nota de ordering del proposal (proposal.md:663-668)
            // no menciona a ALTER TABLE gastos en su lista explícita — el texto vinculante de la
            // tarea 1.10 delega a "si el proposal ordena distinto, seguí al proposal" en ese
            // caso. `dotnet ef migrations add` coloca este ALTER acá por el mismo motivo (orden
            // topológico correcto); se preserva esa posición. Estructuralmente inviolable
            // (id_gasto ya único vía pk_gastos): +1 índice único implícito (conteo total = 7).
            migrationBuilder.AddUniqueConstraint(
                name: "ak_gastos_id_gasto_id_tenant",
                table: "gastos",
                columns: new[] { "id_gasto", "id_tenant" });

            migrationBuilder.CreateTable(
                name: "movimientos_cuenta_corriente_proveedor",
                columns: table => new
                {
                    id_movimiento = table.Column<int>(type: "integer", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    id_tenant = table.Column<int>(type: "integer", nullable: false),
                    id_proveedor = table.Column<int>(type: "integer", nullable: false),
                    fecha = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    id_punto_venta = table.Column<int>(type: "integer", nullable: true),
                    id_empleado = table.Column<int>(type: "integer", nullable: true),
                    tipo = table.Column<TipoMovimientoCcProveedor>(type: "tipo_movimiento_cc_proveedor", nullable: false),
                    id_comprobante_compra = table.Column<int>(type: "integer", nullable: true),
                    id_gasto = table.Column<int>(type: "integer", nullable: true),
                    importe = table.Column<decimal>(type: "numeric(14,2)", nullable: false),
                    saldo_resultante = table.Column<decimal>(type: "numeric(14,2)", nullable: false),
                    detalle = table.Column<string>(type: "text", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_movimientos_cuenta_corriente_proveedor", x => x.id_movimiento);
                    table.CheckConstraint("ck_movimientos_cuenta_corriente_proveedor_apertura", "(tipo = 'apertura' AND id_punto_venta IS NULL AND id_empleado IS NULL) OR (tipo <> 'apertura' AND id_punto_venta IS NOT NULL AND id_empleado IS NOT NULL)");
                    table.ForeignKey(
                        name: "fk_movimientos_cuenta_corriente_proveedor_comprobante_compra",
                        columns: x => new { x.id_comprobante_compra, x.id_tenant },
                        principalTable: "comprobantes_compra",
                        principalColumns: new[] { "id_comprobante_compra", "id_tenant" },
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "fk_movimientos_cuenta_corriente_proveedor_empleado",
                        column: x => x.id_empleado,
                        principalTable: "usuarios",
                        principalColumn: "id_usuario",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "fk_movimientos_cuenta_corriente_proveedor_gasto",
                        columns: x => new { x.id_gasto, x.id_tenant },
                        principalTable: "gastos",
                        principalColumns: new[] { "id_gasto", "id_tenant" },
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "fk_movimientos_cuenta_corriente_proveedor_proveedor",
                        columns: x => new { x.id_proveedor, x.id_tenant },
                        principalTable: "proveedores",
                        principalColumns: new[] { "id_proveedor", "id_tenant" },
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "fk_movimientos_cuenta_corriente_proveedor_punto_venta",
                        columns: x => new { x.id_punto_venta, x.id_tenant },
                        principalTable: "puntos_venta",
                        principalColumns: new[] { "id_punto_venta", "id_tenant" },
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "fk_movimientos_cuenta_corriente_proveedor_tenant",
                        column: x => x.id_tenant,
                        principalTable: "tenants",
                        principalColumn: "id_tenant",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateIndex(
                name: "ix_movimientos_cuenta_corriente_proveedor_comprobante_compra",
                table: "movimientos_cuenta_corriente_proveedor",
                columns: new[] { "id_comprobante_compra", "id_tenant" });

            migrationBuilder.CreateIndex(
                name: "ix_movimientos_cuenta_corriente_proveedor_empleado",
                table: "movimientos_cuenta_corriente_proveedor",
                column: "id_empleado");

            migrationBuilder.CreateIndex(
                name: "ix_movimientos_cuenta_corriente_proveedor_gasto",
                table: "movimientos_cuenta_corriente_proveedor",
                columns: new[] { "id_gasto", "id_tenant" });

            migrationBuilder.CreateIndex(
                name: "ix_movimientos_cuenta_corriente_proveedor_proveedor_fecha",
                table: "movimientos_cuenta_corriente_proveedor",
                columns: new[] { "id_proveedor", "id_tenant", "fecha" });

            migrationBuilder.CreateIndex(
                name: "ix_movimientos_cuenta_corriente_proveedor_punto_venta",
                table: "movimientos_cuenta_corriente_proveedor",
                columns: new[] { "id_punto_venta", "id_tenant" });

            migrationBuilder.CreateIndex(
                name: "ix_movimientos_cuenta_corriente_proveedor_tenant",
                table: "movimientos_cuenta_corriente_proveedor",
                column: "id_tenant");

            // gate §C, statement 1 (proposal.md:606-635, VERBATIM): asiento de apertura — la
            // fórmula EXACTA del spec saldo-de-proveedor que esta etapa retira. Idempotente via
            // NOT EXISTS (guard, tarea 1.31/target #7); deleted_at IS NULL respeta soft deletes
            // en los tres lados (comprobantes_compra/gastos/proveedores, targets #1-#3); WHERE
            // d.saldo <> 0 mantiene la migración proporcional a actividad real (target #6).
            migrationBuilder.Sql(
                """
                WITH derivado AS (
                    SELECT p.id_tenant,
                           p.id_proveedor,
                           COALESCE(c.total, 0) - COALESCE(g.total, 0) AS saldo
                    FROM proveedores p
                    LEFT JOIN (SELECT id_tenant, id_proveedor, SUM(total) AS total
                               FROM comprobantes_compra
                               WHERE estado = 'confirmada' AND deleted_at IS NULL
                               GROUP BY id_tenant, id_proveedor) c
                           ON c.id_tenant = p.id_tenant AND c.id_proveedor = p.id_proveedor
                    LEFT JOIN (SELECT id_tenant, id_proveedor, SUM(importe) AS total
                               FROM gastos
                               WHERE categoria = 'proveedor' AND id_proveedor IS NOT NULL AND deleted_at IS NULL
                               GROUP BY id_tenant, id_proveedor) g
                           ON g.id_tenant = p.id_tenant AND g.id_proveedor = p.id_proveedor
                    WHERE p.deleted_at IS NULL
                )
                INSERT INTO movimientos_cuenta_corriente_proveedor
                    (id_tenant, id_proveedor, fecha, id_punto_venta, id_empleado, tipo,
                     id_comprobante_compra, id_gasto, importe, saldo_resultante, detalle)
                SELECT d.id_tenant, d.id_proveedor, now(), NULL, NULL, 'apertura',
                       NULL, NULL, d.saldo, d.saldo,
                       'Asiento de apertura (etapa 15): saldo derivado de compras confirmadas menos gastos '
                       || 'de categoria proveedor al momento de la migracion.'
                FROM derivado d
                WHERE d.saldo <> 0
                  AND NOT EXISTS (SELECT 1
                                  FROM movimientos_cuenta_corriente_proveedor m
                                  WHERE m.id_tenant = d.id_tenant AND m.id_proveedor = d.id_proveedor);
                """);

            // gate §C: ALTER TABLE proveedores ADD COLUMN saldo (metadata-only, PG 11+, sin
            // rewrite de tabla) — DESPUÉS del statement 1 (no lo necesita) y ANTES del
            // statement 2 (lo necesita: actualiza esta misma columna).
            migrationBuilder.AddColumn<decimal>(
                name: "saldo",
                table: "proveedores",
                type: "numeric(14,2)",
                nullable: false,
                defaultValue: 0m);

            // gate §C, statement 2 (proposal.md:637-645, VERBATIM): el cache, derivado DE la
            // fila que el statement 1 acaba de escribir — nunca recalculado aparte (target #8).
            migrationBuilder.Sql(
                """
                UPDATE proveedores p
                   SET saldo = m.saldo_resultante
                  FROM movimientos_cuenta_corriente_proveedor m
                 WHERE m.id_tenant = p.id_tenant
                   AND m.id_proveedor = p.id_proveedor
                   AND m.tipo = 'apertura'
                   AND p.saldo <> m.saldo_resultante;
                """);

            // RLS al final (proposal.md:663-668, ADR-15): la policy es FORCEd y la conexión de
            // migración no tiene app_tenant_actual() seteado — activar RLS antes del backfill
            // haría que la corrección dependiera del carryover superusuario de ways_owner en vez
            // de ser una garantía real. Target #9/#11.
            migrationBuilder.HabilitarRlsDeTenant("movimientos_cuenta_corriente_proveedor");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "movimientos_cuenta_corriente_proveedor");

            migrationBuilder.DropUniqueConstraint(
                name: "ak_gastos_id_gasto_id_tenant",
                table: "gastos");

            migrationBuilder.DropColumn(
                name: "saldo",
                table: "proveedores");

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
