using Microsoft.EntityFrameworkCore.Migrations;
using Ways.Domain.Gastos;

#nullable disable

namespace Ways.Infrastructure.Persistencia.Migraciones
{
    /// <inheritdoc />
    public partial class GastosOrigenFondosYTesoreriaPorEmpresa : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AlterDatabase()
                .Annotation("Npgsql:Enum:ambiente_fiscal", "homologacion,produccion")
                .Annotation("Npgsql:Enum:categoria_gasto", "impuestos,otros,proveedor,servicios,sueldos,viaticos")
                .Annotation("Npgsql:Enum:clase_comprobante", "compra,venta")
                .Annotation("Npgsql:Enum:comportamiento_medio_pago", "cuenta_corriente,efectivo,electronico")
                .Annotation("Npgsql:Enum:estado_compra", "anulada,borrador,confirmada")
                .Annotation("Npgsql:Enum:estado_comprobante", "anulado,emitido")
                .Annotation("Npgsql:Enum:estado_orden_compra", "anulada,borrador,cerrada,enviada,recibida_parcial")
                .Annotation("Npgsql:Enum:estado_presupuesto", "anulado,borrador,convertido,enviado")
                .Annotation("Npgsql:Enum:estado_remito", "anulado,borrador,emitido,facturado")
                .Annotation("Npgsql:Enum:estado_tenant", "activo,baja,suspendido")
                .Annotation("Npgsql:Enum:estado_turno", "abierto,cerrado")
                .Annotation("Npgsql:Enum:estado_usuario", "activo,bloqueado,inactivo")
                .Annotation("Npgsql:Enum:modo_lista", "derivada,fija")
                .Annotation("Npgsql:Enum:modo_punto_venta", "escritorio,web")
                .Annotation("Npgsql:Enum:motivo_stock", "ajuste,anulacion,compra,decomiso,inventario,reclasificacion,remito,transferencia,venta")
                .Annotation("Npgsql:Enum:origen_fondos_gasto", "caja_turno,tesoreria")
                .Annotation("Npgsql:Enum:resultado_fiscal", "aprobado,aprobado_con_observaciones,pendiente,rechazado")
                .Annotation("Npgsql:Enum:tipo_documento", "cuil,cuit,dni,otro,pasaporte")
                .Annotation("Npgsql:Enum:tipo_movimiento_caja", "apertura_cajon,refuerzo,retiro")
                .Annotation("Npgsql:Enum:tipo_movimiento_cc", "actualizacion_precios,ajuste,consumo,pago")
                .Annotation("Npgsql:Enum:tipo_movimiento_cc_proveedor", "ajuste,apertura,compra,pago")
                .Annotation("Npgsql:Enum:tipo_movimiento_tesoreria", "ajuste,deposito,gasto,retiro_caja")
                .Annotation("Npgsql:Enum:unidad_venta", "peso,unidad")
                .Annotation("Npgsql:PostgresExtension:citext", ",,")
                .OldAnnotation("Npgsql:Enum:ambiente_fiscal", "homologacion,produccion")
                .OldAnnotation("Npgsql:Enum:categoria_gasto", "impuestos,otros,proveedor,servicios,sueldos,viaticos")
                .OldAnnotation("Npgsql:Enum:clase_comprobante", "compra,venta")
                .OldAnnotation("Npgsql:Enum:comportamiento_medio_pago", "cuenta_corriente,efectivo,electronico")
                .OldAnnotation("Npgsql:Enum:estado_compra", "anulada,borrador,confirmada")
                .OldAnnotation("Npgsql:Enum:estado_comprobante", "anulado,emitido")
                .OldAnnotation("Npgsql:Enum:estado_orden_compra", "anulada,borrador,cerrada,enviada,recibida_parcial")
                .OldAnnotation("Npgsql:Enum:estado_presupuesto", "anulado,borrador,convertido,enviado")
                .OldAnnotation("Npgsql:Enum:estado_remito", "anulado,borrador,emitido,facturado")
                .OldAnnotation("Npgsql:Enum:estado_tenant", "activo,baja,suspendido")
                .OldAnnotation("Npgsql:Enum:estado_turno", "abierto,cerrado")
                .OldAnnotation("Npgsql:Enum:estado_usuario", "activo,bloqueado,inactivo")
                .OldAnnotation("Npgsql:Enum:modo_lista", "derivada,fija")
                .OldAnnotation("Npgsql:Enum:modo_punto_venta", "escritorio,web")
                .OldAnnotation("Npgsql:Enum:motivo_stock", "ajuste,anulacion,compra,decomiso,inventario,reclasificacion,remito,transferencia,venta")
                .OldAnnotation("Npgsql:Enum:resultado_fiscal", "aprobado,aprobado_con_observaciones,pendiente,rechazado")
                .OldAnnotation("Npgsql:Enum:tipo_documento", "cuil,cuit,dni,otro,pasaporte")
                .OldAnnotation("Npgsql:Enum:tipo_movimiento_caja", "apertura_cajon,refuerzo,retiro")
                .OldAnnotation("Npgsql:Enum:tipo_movimiento_cc", "actualizacion_precios,ajuste,consumo,pago")
                .OldAnnotation("Npgsql:Enum:tipo_movimiento_cc_proveedor", "ajuste,apertura,compra,pago")
                .OldAnnotation("Npgsql:Enum:tipo_movimiento_tesoreria", "ajuste,deposito,gasto,retiro_caja")
                .OldAnnotation("Npgsql:Enum:unidad_venta", "peso,unidad")
                .OldAnnotation("Npgsql:PostgresExtension:citext", ",,");

            migrationBuilder.AlterColumn<int>(
                name: "id_punto_venta",
                table: "movimientos_tesoreria",
                type: "integer",
                nullable: true,
                oldClrType: typeof(int),
                oldType: "integer");

            migrationBuilder.AddColumn<int>(
                name: "id_empresa",
                table: "movimientos_tesoreria",
                type: "integer",
                nullable: false,
                defaultValue: 0);

            migrationBuilder.AddColumn<int>(
                name: "id_gasto",
                table: "movimientos_tesoreria",
                type: "integer",
                nullable: true);

            migrationBuilder.AlterColumn<int>(
                name: "id_turno_caja",
                table: "gastos",
                type: "integer",
                nullable: true,
                oldClrType: typeof(int),
                oldType: "integer");

            migrationBuilder.AlterColumn<int>(
                name: "id_punto_venta",
                table: "gastos",
                type: "integer",
                nullable: true,
                oldClrType: typeof(int),
                oldType: "integer");

            migrationBuilder.AddColumn<int>(
                name: "id_empresa",
                table: "gastos",
                type: "integer",
                nullable: false,
                defaultValue: 0);

            // Nullable, SIN defaultValue a propósito (mismo gate que
            // ModoPuntoVentaYDispositivoActivoUnico §2 para modo_punto_venta): un AddColumn con
            // defaultValue sobre un enum nativo de Postgres le pide a Npgsql un literal tipado
            // (`'caja_turno'::origen_fondos_gasto`) que el generador de SQL de AddColumnOperation
            // no produce — emite el 0 crudo del enum subyacente y Postgres lo rechaza
            // (42804: "default expression is of type integer"). Tres pasos: nullable -> backfill
            // -> SET NOT NULL.
            migrationBuilder.AddColumn<OrigenFondosGasto>(
                name: "origen_fondos",
                table: "gastos",
                type: "origen_fondos_gasto",
                nullable: true);

            // Backfill 1/5 (rls-migration-backfills): toda fila preexistente es, por definición,
            // caja_turno — es el único origen que ServicioDeGastos emitió hasta este PR. Mismo
            // SET LOCAL que el resto (gastos corre bajo FORCE ROW LEVEL SECURITY).
            migrationBuilder.Sql(
                """
                SET LOCAL app.acceso = 'plataforma';

                UPDATE gastos
                   SET origen_fondos = 'caja_turno'::origen_fondos_gasto
                 WHERE origen_fondos IS NULL;
                """);

            migrationBuilder.AlterColumn<OrigenFondosGasto>(
                name: "origen_fondos",
                table: "gastos",
                type: "origen_fondos_gasto",
                nullable: false,
                oldClrType: typeof(OrigenFondosGasto),
                oldType: "origen_fondos_gasto",
                oldNullable: true);

            // Backfill 2/5 (rls-migration-backfills): gastos.id_empresa quedó en 0 (el
            // defaultValue de la AddColumn de arriba) para toda fila preexistente — gastos/
            // movimientos_tesoreria corren bajo FORCE ROW LEVEL SECURITY
            // (HabilitarRlsDeTenant en TurnosCajaYGastosEtapa6) y el camino de deploy
            // (dotnet ef database update vía WaysDbContextFactory) no registra el interceptor
            // de tenant, así que el SET LOCAL vive DENTRO de este mismo bloque Sql(). Idempotente
            // por el guard `id_empresa = 0`: una corrida repetida no vuelve a tocar filas ya
            // completadas.
            migrationBuilder.Sql(
                """
                SET LOCAL app.acceso = 'plataforma';

                UPDATE gastos g
                   SET id_empresa = pv.id_empresa
                  FROM puntos_venta pv
                 WHERE pv.id_punto_venta = g.id_punto_venta
                   AND pv.id_tenant = g.id_tenant
                   AND g.id_empresa = 0;
                """);

            // Backfill 3/5: mismo criterio que el de arriba, para movimientos_tesoreria.
            migrationBuilder.Sql(
                """
                SET LOCAL app.acceso = 'plataforma';

                UPDATE movimientos_tesoreria m
                   SET id_empresa = pv.id_empresa
                  FROM puntos_venta pv
                 WHERE pv.id_punto_venta = m.id_punto_venta
                   AND pv.id_tenant = m.id_tenant
                   AND m.id_empresa = 0;
                """);

            // Backfill 4/5: re-ancla la cadena inicio→final de (id_punto_venta) a
            // (id_tenant, id_empresa) — varios puntos de venta de la misma empresa pasan a
            // compartir UN fondo. Recomputa `final` como la suma acumulada de (ingreso − egreso)
            // ordenada por (fecha, id_movimiento) DENTRO de cada partición (id_tenant, id_empresa)
            // — la primera fila de cada empresa arranca en inicio = 0, igual que hoy arranca en 0
            // la primera fila de cada punto de venta (ServicioDeTurnos.InsertarArqueosYTesoreriaAsync,
            // FirstOrDefaultAsync sin fila previa). `inicio` se despeja de la MISMA suma acumulada
            // (final_acumulado − (ingreso−egreso) de la propia fila), así que la igualdad
            // `final = inicio + ingreso - egreso` de ck_movimientos_tesoreria_cadena se preserva
            // por construcción para cada fila, sin excepción. Idempotente por convergencia, no por
            // exclusión: ingreso/egreso no cambian, así que recorrer este UPDATE de nuevo recalcula
            // exactamente los mismos valores (no hace falta ningún WHERE que excluya filas ya
            // completadas).
            migrationBuilder.Sql(
                """
                SET LOCAL app.acceso = 'plataforma';

                WITH cadena AS (
                    SELECT
                        id_movimiento,
                        SUM(ingreso - egreso) OVER (
                            PARTITION BY id_tenant, id_empresa
                            ORDER BY fecha, id_movimiento
                            ROWS BETWEEN UNBOUNDED PRECEDING AND CURRENT ROW
                        ) AS final_acumulado
                    FROM movimientos_tesoreria
                )
                UPDATE movimientos_tesoreria m
                   SET inicio = c.final_acumulado - (m.ingreso - m.egreso),
                       final  = c.final_acumulado
                  FROM cadena c
                 WHERE c.id_movimiento = m.id_movimiento;
                """);

            // Backfill 5/5: el DEFAULT 0 de las dos AddColumn de arriba es un artefacto de
            // migración (EF exige un defaultValue para agregar una columna NOT NULL a una tabla
            // con filas) — una vez que los backfills 2/3 dejaron cada fila con su id_empresa real,
            // el DEFAULT deja de tener sentido: un INSERT futuro que omita la columna por
            // accidente tiene que fallar por NOT NULL, nunca colar un id_empresa=0 inexistente que
            // dispare 23503 en el mejor caso (o, peor, apunte a una empresa 0 real de otro
            // contexto). DROP DEFAULT no toca filas existentes, solo el comportamiento de un
            // INSERT futuro sin esa columna.
            migrationBuilder.Sql("ALTER TABLE gastos ALTER COLUMN id_empresa DROP DEFAULT;");
            migrationBuilder.Sql("ALTER TABLE movimientos_tesoreria ALTER COLUMN id_empresa DROP DEFAULT;");

            migrationBuilder.CreateIndex(
                name: "ix_movimientos_tesoreria_empresa_id",
                table: "movimientos_tesoreria",
                columns: new[] { "id_empresa", "id_tenant", "id_movimiento" });

            migrationBuilder.CreateIndex(
                name: "ix_movimientos_tesoreria_gasto",
                table: "movimientos_tesoreria",
                columns: new[] { "id_gasto", "id_tenant" });

            migrationBuilder.CreateIndex(
                name: "ux_movimientos_tesoreria_id_gasto",
                table: "movimientos_tesoreria",
                column: "id_gasto",
                unique: true,
                filter: "id_gasto IS NOT NULL");

            migrationBuilder.CreateIndex(
                name: "ix_gastos_empresa",
                table: "gastos",
                columns: new[] { "id_empresa", "id_tenant" });

            migrationBuilder.AddCheckConstraint(
                name: "ck_gastos_caja_turno_requiere_turno",
                table: "gastos",
                sql: "origen_fondos <> 'caja_turno' OR id_turno_caja IS NOT NULL");

            migrationBuilder.AddForeignKey(
                name: "fk_gastos_empresa",
                table: "gastos",
                columns: new[] { "id_empresa", "id_tenant" },
                principalTable: "empresas",
                principalColumns: new[] { "id_empresa", "id_tenant" },
                onDelete: ReferentialAction.Restrict);

            migrationBuilder.AddForeignKey(
                name: "fk_movimientos_tesoreria_empresa",
                table: "movimientos_tesoreria",
                columns: new[] { "id_empresa", "id_tenant" },
                principalTable: "empresas",
                principalColumns: new[] { "id_empresa", "id_tenant" },
                onDelete: ReferentialAction.Restrict);

            migrationBuilder.AddForeignKey(
                name: "fk_movimientos_tesoreria_gasto",
                table: "movimientos_tesoreria",
                columns: new[] { "id_gasto", "id_tenant" },
                principalTable: "gastos",
                principalColumns: new[] { "id_gasto", "id_tenant" },
                onDelete: ReferentialAction.Restrict);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropForeignKey(
                name: "fk_gastos_empresa",
                table: "gastos");

            migrationBuilder.DropForeignKey(
                name: "fk_movimientos_tesoreria_empresa",
                table: "movimientos_tesoreria");

            migrationBuilder.DropForeignKey(
                name: "fk_movimientos_tesoreria_gasto",
                table: "movimientos_tesoreria");

            migrationBuilder.DropIndex(
                name: "ix_movimientos_tesoreria_empresa_id",
                table: "movimientos_tesoreria");

            migrationBuilder.DropIndex(
                name: "ix_movimientos_tesoreria_gasto",
                table: "movimientos_tesoreria");

            migrationBuilder.DropIndex(
                name: "ux_movimientos_tesoreria_id_gasto",
                table: "movimientos_tesoreria");

            migrationBuilder.DropIndex(
                name: "ix_gastos_empresa",
                table: "gastos");

            migrationBuilder.DropCheckConstraint(
                name: "ck_gastos_caja_turno_requiere_turno",
                table: "gastos");

            migrationBuilder.DropColumn(
                name: "id_empresa",
                table: "movimientos_tesoreria");

            migrationBuilder.DropColumn(
                name: "id_gasto",
                table: "movimientos_tesoreria");

            migrationBuilder.DropColumn(
                name: "id_empresa",
                table: "gastos");

            migrationBuilder.DropColumn(
                name: "origen_fondos",
                table: "gastos");

            migrationBuilder.AlterDatabase()
                .Annotation("Npgsql:Enum:ambiente_fiscal", "homologacion,produccion")
                .Annotation("Npgsql:Enum:categoria_gasto", "impuestos,otros,proveedor,servicios,sueldos,viaticos")
                .Annotation("Npgsql:Enum:clase_comprobante", "compra,venta")
                .Annotation("Npgsql:Enum:comportamiento_medio_pago", "cuenta_corriente,efectivo,electronico")
                .Annotation("Npgsql:Enum:estado_compra", "anulada,borrador,confirmada")
                .Annotation("Npgsql:Enum:estado_comprobante", "anulado,emitido")
                .Annotation("Npgsql:Enum:estado_orden_compra", "anulada,borrador,cerrada,enviada,recibida_parcial")
                .Annotation("Npgsql:Enum:estado_presupuesto", "anulado,borrador,convertido,enviado")
                .Annotation("Npgsql:Enum:estado_remito", "anulado,borrador,emitido,facturado")
                .Annotation("Npgsql:Enum:estado_tenant", "activo,baja,suspendido")
                .Annotation("Npgsql:Enum:estado_turno", "abierto,cerrado")
                .Annotation("Npgsql:Enum:estado_usuario", "activo,bloqueado,inactivo")
                .Annotation("Npgsql:Enum:modo_lista", "derivada,fija")
                .Annotation("Npgsql:Enum:modo_punto_venta", "escritorio,web")
                .Annotation("Npgsql:Enum:motivo_stock", "ajuste,anulacion,compra,decomiso,inventario,reclasificacion,remito,transferencia,venta")
                .Annotation("Npgsql:Enum:resultado_fiscal", "aprobado,aprobado_con_observaciones,pendiente,rechazado")
                .Annotation("Npgsql:Enum:tipo_documento", "cuil,cuit,dni,otro,pasaporte")
                .Annotation("Npgsql:Enum:tipo_movimiento_caja", "apertura_cajon,refuerzo,retiro")
                .Annotation("Npgsql:Enum:tipo_movimiento_cc", "actualizacion_precios,ajuste,consumo,pago")
                .Annotation("Npgsql:Enum:tipo_movimiento_cc_proveedor", "ajuste,apertura,compra,pago")
                .Annotation("Npgsql:Enum:tipo_movimiento_tesoreria", "ajuste,deposito,gasto,retiro_caja")
                .Annotation("Npgsql:Enum:unidad_venta", "peso,unidad")
                .Annotation("Npgsql:PostgresExtension:citext", ",,")
                .OldAnnotation("Npgsql:Enum:ambiente_fiscal", "homologacion,produccion")
                .OldAnnotation("Npgsql:Enum:categoria_gasto", "impuestos,otros,proveedor,servicios,sueldos,viaticos")
                .OldAnnotation("Npgsql:Enum:clase_comprobante", "compra,venta")
                .OldAnnotation("Npgsql:Enum:comportamiento_medio_pago", "cuenta_corriente,efectivo,electronico")
                .OldAnnotation("Npgsql:Enum:estado_compra", "anulada,borrador,confirmada")
                .OldAnnotation("Npgsql:Enum:estado_comprobante", "anulado,emitido")
                .OldAnnotation("Npgsql:Enum:estado_orden_compra", "anulada,borrador,cerrada,enviada,recibida_parcial")
                .OldAnnotation("Npgsql:Enum:estado_presupuesto", "anulado,borrador,convertido,enviado")
                .OldAnnotation("Npgsql:Enum:estado_remito", "anulado,borrador,emitido,facturado")
                .OldAnnotation("Npgsql:Enum:estado_tenant", "activo,baja,suspendido")
                .OldAnnotation("Npgsql:Enum:estado_turno", "abierto,cerrado")
                .OldAnnotation("Npgsql:Enum:estado_usuario", "activo,bloqueado,inactivo")
                .OldAnnotation("Npgsql:Enum:modo_lista", "derivada,fija")
                .OldAnnotation("Npgsql:Enum:modo_punto_venta", "escritorio,web")
                .OldAnnotation("Npgsql:Enum:motivo_stock", "ajuste,anulacion,compra,decomiso,inventario,reclasificacion,remito,transferencia,venta")
                .OldAnnotation("Npgsql:Enum:origen_fondos_gasto", "caja_turno,tesoreria")
                .OldAnnotation("Npgsql:Enum:resultado_fiscal", "aprobado,aprobado_con_observaciones,pendiente,rechazado")
                .OldAnnotation("Npgsql:Enum:tipo_documento", "cuil,cuit,dni,otro,pasaporte")
                .OldAnnotation("Npgsql:Enum:tipo_movimiento_caja", "apertura_cajon,refuerzo,retiro")
                .OldAnnotation("Npgsql:Enum:tipo_movimiento_cc", "actualizacion_precios,ajuste,consumo,pago")
                .OldAnnotation("Npgsql:Enum:tipo_movimiento_cc_proveedor", "ajuste,apertura,compra,pago")
                .OldAnnotation("Npgsql:Enum:tipo_movimiento_tesoreria", "ajuste,deposito,gasto,retiro_caja")
                .OldAnnotation("Npgsql:Enum:unidad_venta", "peso,unidad")
                .OldAnnotation("Npgsql:PostgresExtension:citext", ",,");

            migrationBuilder.AlterColumn<int>(
                name: "id_punto_venta",
                table: "movimientos_tesoreria",
                type: "integer",
                nullable: false,
                defaultValue: 0,
                oldClrType: typeof(int),
                oldType: "integer",
                oldNullable: true);

            migrationBuilder.AlterColumn<int>(
                name: "id_turno_caja",
                table: "gastos",
                type: "integer",
                nullable: false,
                defaultValue: 0,
                oldClrType: typeof(int),
                oldType: "integer",
                oldNullable: true);

            migrationBuilder.AlterColumn<int>(
                name: "id_punto_venta",
                table: "gastos",
                type: "integer",
                nullable: false,
                defaultValue: 0,
                oldClrType: typeof(int),
                oldType: "integer",
                oldNullable: true);
        }
    }
}
