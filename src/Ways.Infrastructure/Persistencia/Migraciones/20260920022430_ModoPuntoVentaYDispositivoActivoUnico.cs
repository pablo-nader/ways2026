using Microsoft.EntityFrameworkCore.Migrations;
using Ways.Domain.Organizacion;

#nullable disable

namespace Ways.Infrastructure.Persistencia.Migraciones
{
    /// <inheritdoc />
    public partial class ModoPuntoVentaYDispositivoActivoUnico : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            // gate §1 (DB CHANGE GATE aprobado): CREATE TYPE modo_punto_venta AS ENUM
            // ('escritorio', 'web') — orden = orden de miembros del enum C# (npgsql.MapEnum<T>()).
            // `dotnet ef migrations add` re-serializa TODAS las anotaciones de enum en orden
            // alfabético (mismo residuo documentado en WaysDbContext.cs:183-186 y en
            // OrdenesDeCompraEtapa16/CuentaCorrienteDeProveedoresEtapa15/PresupuestosEtapa17) — acá
            // no hace falta corregir nada a mano porque "escritorio" < "web" alfabéticamente
            // coincide con el orden declarado (Escritorio = 0, Web = 1); las demás anotaciones son
            // Old == New (tipos preexistentes, sin cambio real) y quedan tal cual las emitió el
            // scaffolder.
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
                .OldAnnotation("Npgsql:Enum:motivo_stock", "ajuste,anulacion,compra,decomiso,inventario,reclasificacion,remito,transferencia,venta")
                .OldAnnotation("Npgsql:Enum:resultado_fiscal", "aprobado,aprobado_con_observaciones,pendiente,rechazado")
                .OldAnnotation("Npgsql:Enum:tipo_documento", "cuil,cuit,dni,otro,pasaporte")
                .OldAnnotation("Npgsql:Enum:tipo_movimiento_caja", "apertura_cajon,refuerzo,retiro")
                .OldAnnotation("Npgsql:Enum:tipo_movimiento_cc", "actualizacion_precios,ajuste,consumo,pago")
                .OldAnnotation("Npgsql:Enum:tipo_movimiento_cc_proveedor", "ajuste,apertura,compra,pago")
                .OldAnnotation("Npgsql:Enum:tipo_movimiento_tesoreria", "ajuste,deposito,gasto,retiro_caja")
                .OldAnnotation("Npgsql:Enum:unidad_venta", "peso,unidad")
                .OldAnnotation("Npgsql:PostgresExtension:citext", ",,");

            // gate §2: `modo` en tres pasos — nullable -> backfill -> SET NOT NULL. SIN default de
            // base a propósito (gate: "el modo se elige al crear el punto de venta"): un
            // `AddColumn` directo con `nullable: false` habría necesitado un `defaultValue` que
            // ningún punto de venta nuevo debe heredar.
            migrationBuilder.AddColumn<ModoPuntoVenta>(
                name: "modo",
                table: "puntos_venta",
                type: "modo_punto_venta",
                nullable: true);

            // Backfill de una sola vez (gate §2): `puntos_venta`/`dispositivos` corren bajo FORCE
            // ROW LEVEL SECURITY y el rol de aplicación no tiene BYPASSRLS en Producción
            // (InicializadorDeBaseDeDatos.VerificarRolSinBypassAsync) — un UPDATE plano afectaría
            // CERO filas y reportaría éxito. El camino de deploy (WaysDbContextFactory, el que usa
            // `dotnet ef database update`) no registra el interceptor de tenant — por eso el
            // SET LOCAL vive en este mismo bloque Sql(), nunca fuera de él (rls-migration-backfills).
            // Idempotente por construcción: `WHERE p.modo IS NULL` excluye las filas ya completadas
            // por una corrida anterior. Un punto de venta con un dispositivo ACTIVO (deleted_at IS
            // NULL) pasa a Escritorio; cualquier otro, a Web.
            migrationBuilder.Sql(
                """
                SET LOCAL app.acceso = 'plataforma';

                UPDATE puntos_venta p
                   SET modo = CASE
                                WHEN EXISTS (
                                    SELECT 1
                                      FROM dispositivos d
                                     WHERE d.id_punto_venta = p.id_punto_venta
                                       AND d.id_tenant = p.id_tenant
                                       AND d.deleted_at IS NULL
                                )
                                THEN 'escritorio'
                                ELSE 'web'
                              END::modo_punto_venta
                 WHERE p.modo IS NULL;
                """);

            migrationBuilder.AlterColumn<ModoPuntoVenta>(
                name: "modo",
                table: "puntos_venta",
                type: "modo_punto_venta",
                nullable: false,
                oldClrType: typeof(ModoPuntoVenta),
                oldType: "modo_punto_venta",
                oldNullable: true);

            // gate §3: la base de desarrollo VIOLA hoy la unicidad que este índice va a exigir
            // (tenant 2/PV 3 y tenant 4/PV 5 con dos dispositivos activos cada uno — leftovers de
            // pruebas E2E). Resolución determinística ANTES de crear el índice, no un `ON CONFLICT`
            // ni un `DISTINCT ON` silencioso: por cada (id_tenant, id_punto_venta) con más de un
            // dispositivo activo, se conserva el de `created_at` más reciente (desempate por
            // `id_dispositivo` DESC) y se da de baja lógica el resto.
            //
            // judgment-day ronda 1 (hallazgo CRITICAL 3): `ultimo_uso_at` NO entra en este orden,
            // a propósito — lo escribe ÚNICAMENTE `RegistrarUsoAsync`, llamado ÚNICAMENTE desde
            // `POST /api/auth/login-dispositivo`. Con sesiones de 365 días, una caja que viene
            // funcionando hace meses puede tener un `ultimo_uso_at` viejísimo (el cajero nunca
            // volvió a loguearse, la cookie sigue viva) mientras que un dispositivo repareado hace
            // un minuto ya tiene un login fresco: ordenar por `ultimo_uso_at` revocaría la caja
            // REALMENTE en uso a favor de la que recién se emparejó. Un duplicado nace de repareear
            // una PC sin revocar el registro viejo — la pareja MÁS RECIENTEMENTE CREADA es la que
            // refleja la intención real del operador, así que `created_at DESC` (con
            // `id_dispositivo DESC` como desempate de filas creadas en el mismo instante) es la
            // única señal confiable acá. Mismo tratamiento de GUC que el backfill de arriba
            // (rls-migration-backfills) — `dispositivos` es tabla de tenant bajo RLS. Idempotente:
            // una corrida repetida ya no encuentra más de una fila activa por partición, así que
            // `orden > 1` nunca es cierto y el UPDATE no toca nada.
            migrationBuilder.Sql(
                """
                SET LOCAL app.acceso = 'plataforma';

                WITH duplicados AS (
                    SELECT id_dispositivo,
                           row_number() OVER (
                               PARTITION BY id_tenant, id_punto_venta
                               ORDER BY created_at DESC, id_dispositivo DESC
                           ) AS orden
                      FROM dispositivos
                     WHERE deleted_at IS NULL
                )
                UPDATE dispositivos d
                   SET deleted_at = now(), updated_at = now()
                  FROM duplicados
                 WHERE d.id_dispositivo = duplicados.id_dispositivo
                   AND duplicados.orden > 1;
                """);

            migrationBuilder.DropIndex(
                name: "ix_dispositivos_tenant_punto_venta",
                table: "dispositivos");

            migrationBuilder.CreateIndex(
                name: "ux_dispositivos_punto_venta_activo",
                table: "dispositivos",
                columns: new[] { "id_tenant", "id_punto_venta" },
                unique: true,
                filter: "deleted_at IS NULL");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            // No-op deliberado (mismo criterio que QuitarVueltoMaximo.Down()): la baja lógica que
            // el gate §3 de Up() aplicó a los dispositivos duplicados es irreversible — no hay
            // manera de reconstruir cuál era "el otro" dispositivo activo de cada punto de venta
            // sin esa información, que ya se perdió al soft-deletearlo. Revertir el esquema de acá
            // para abajo no revive esas filas.
            migrationBuilder.DropIndex(
                name: "ux_dispositivos_punto_venta_activo",
                table: "dispositivos");

            migrationBuilder.CreateIndex(
                name: "ix_dispositivos_tenant_punto_venta",
                table: "dispositivos",
                columns: new[] { "id_tenant", "id_punto_venta" });

            migrationBuilder.DropColumn(
                name: "modo",
                table: "puntos_venta");

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
                .OldAnnotation("Npgsql:Enum:resultado_fiscal", "aprobado,aprobado_con_observaciones,pendiente,rechazado")
                .OldAnnotation("Npgsql:Enum:tipo_documento", "cuil,cuit,dni,otro,pasaporte")
                .OldAnnotation("Npgsql:Enum:tipo_movimiento_caja", "apertura_cajon,refuerzo,retiro")
                .OldAnnotation("Npgsql:Enum:tipo_movimiento_cc", "actualizacion_precios,ajuste,consumo,pago")
                .OldAnnotation("Npgsql:Enum:tipo_movimiento_cc_proveedor", "ajuste,apertura,compra,pago")
                .OldAnnotation("Npgsql:Enum:tipo_movimiento_tesoreria", "ajuste,deposito,gasto,retiro_caja")
                .OldAnnotation("Npgsql:Enum:unidad_venta", "peso,unidad")
                .OldAnnotation("Npgsql:PostgresExtension:citext", ",,");
        }
    }
}
