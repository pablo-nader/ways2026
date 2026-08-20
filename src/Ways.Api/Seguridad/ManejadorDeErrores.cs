using Microsoft.AspNetCore.Diagnostics;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Npgsql;
using Ways.Domain.Common;

namespace Ways.Api.Seguridad;

/// <summary>
/// Traduce los <see cref="ErrorDominio"/> a ProblemDetails con su código de negocio,
/// y cualquier otra excepción a un 500 genérico sin filtrar detalles internos.
/// </summary>
public class ManejadorDeErrores(
    IProblemDetailsService problemDetails,
    ILogger<ManejadorDeErrores> log) : IExceptionHandler
{
    public async ValueTask<bool> TryHandleAsync(
        HttpContext contexto, Exception excepcion, CancellationToken ct)
    {
        var (estado, titulo, codigo) = excepcion switch
        {
            ErrorDominio e => (e.EstadoHttp, e.Message, e.Codigo),

            // Camino EF SaveChangesAsync: Npgsql envuelve la excepción en DbUpdateException.
            // ClasificarPostgresException (helper compartido, más abajo) es la ÚNICA fuente de
            // verdad de la clasificación — este brazo y el de abajo (camino raw-ADO) llaman
            // exactamente al mismo método, misma prioridad de resolución, mismo resultado para
            // el mismo SqlState/ConstraintName.
            DbUpdateException { InnerException: PostgresException pgEnvuelta }
                when ClasificarPostgresException(pgEnvuelta, log) is { } backstopEf =>
                backstopEf,

            // fix(raw-ado): camino raw-ADO (conexion.CreateCommand() en ServicioDeVentas/
            // ServicioDeCompras/ServicioDeStock/ServicioDeLotes) — Npgsql tira PostgresException
            // PELADA, nunca envuelta en DbUpdateException (esa envoltura es específica de
            // SaveChangesAsync). Sin este brazo, cualquier violación de constraint disparada
            // desde uno de esos statements crudos caía derecho al catch-all de abajo como 500
            // error_interno — detectado dos veces en el judgment-day de la etapa 12
            // (ck_movimientos_stock_cantidad_no_cero y fk_stock_lotes_lote). Mismo helper que el
            // brazo de arriba: un solo lugar de verdad para los dos caminos de escritura.
            PostgresException pgCruda when ClasificarPostgresException(pgCruda, log) is { } backstopCrudo =>
                backstopCrudo,

            // Defensa en profundidad genérica (judgment-day, item 2, stage-4-ofertas): EF
            // interpreta un UPDATE/DELETE que afecta 0 filas de las esperadas (en vez de la 1
            // esperada por su predicado de PK) como un conflicto de concurrencia y lanza
            // DbUpdateConcurrencyException — p.ej. un segundo escritor cuyo DELETE apunta a filas
            // que otro escritor ya borró y comiteó primero. Sin este caso, eso llegaba como 500
            // crudo en vez de un 409 traducido.
            //
            // Colocado ACÁ, DESPUÉS del brazo `DbUpdateException { InnerException:
            // PostgresException }` de arriba y ANTES del catch-all `_`, en vez de arriba de todo
            // junto a `ErrorDominio`: como `DbUpdateConcurrencyException` DERIVA de
            // `DbUpdateException`, esta posición es la única que estructuralmente GARANTIZA que
            // nunca puede eclipsar ese caso más específico (que exige además un `InnerException`
            // de tipo `PostgresException` clasificable — si alguna vez `DbUpdateConcurrencyException`
            // llegara a traer ese mismo shape de `InnerException`, el switch ya lo habría resuelto
            // arriba antes de llegar acá). Genérico a propósito (no ofertas-específico): cualquier
            // replace-set/edición concurrente que EF detecte como "0 filas afectadas" en
            // cualquier tabla cae acá con el mismo código estable.
            DbUpdateConcurrencyException =>
                (StatusCodes.Status409Conflict, "El registro fue modificado por otra operación concurrente; reintentá la operación.", "edicion_concurrente"),

            _ => (StatusCodes.Status500InternalServerError,
                  "Ocurrió un error inesperado.",
                  "error_interno")
        };

        if (estado >= 500)
        {
            log.LogError(excepcion, "Error no controlado en {Ruta}.", contexto.Request.Path);
        }

        contexto.Response.StatusCode = estado;

        return await problemDetails.TryWriteAsync(new ProblemDetailsContext
        {
            HttpContext = contexto,
            Exception = excepcion,
            ProblemDetails = new ProblemDetails
            {
                Status = estado,
                Title = titulo,
                Extensions = { ["codigo"] = codigo }
            }
        });
    }

    /// <summary>fix(raw-ado): único punto de clasificación de un <see cref="PostgresException"/>
    /// por SqlState/ConstraintName — extraído del switch de <see cref="TryHandleAsync"/> para que
    /// el camino EF (<c>DbUpdateException.InnerException</c>) y el camino raw-ADO (excepción
    /// pelada) llamen exactamente al mismo método, misma prioridad de resolución. Cada <c>when</c>
    /// de acá abajo es EL MISMO chequeo que tenía el `case` original — solo se movió de matchear
    /// contra <c>DbUpdateException { InnerException: PostgresException {...} }</c> a matchear
    /// directo contra <paramref name="pg"/>; el orden entre casos (crítico para los "ordering
    /// trap" documentados en cada uno: exact-match ANTES que el Contains-chain genérico de
    /// <see cref="ClasificarUnicidad"/>) es IDÉNTICO al que tenía el switch antes de esta
    /// extracción — cero cambio de comportamiento en el camino EF, que es el único que corría
    /// hasta ahora.</summary>
    private static (int EstadoHttp, string Titulo, string Codigo)? ClasificarPostgresException(
        PostgresException pg, ILogger log) =>
        pg switch
        {
            // Backstop de la carrera entre el chequeo previo de `ServicioDeUsuarios` y el
            // `SaveChangesAsync`: dos requests concurrentes pueden pasar el chequeo y chocar
            // recién acá. Traduce el mismo 409 de negocio en vez de dejar pasar un 500 genérico
            // (que además sería un oráculo de enumeración cross-tenant: 409 vs 500 delataría si
            // el mail ya existe en otro tenant).
            { SqlState: "23505", ConstraintName: "ux_usuarios_mail" } =>
                (StatusCodes.Status409Conflict, "El mail ya está en uso.", "mail_duplicado"),

            // Mismo backstop que el de arriba, para la otra unicidad de `usuarios`
            // (`usuario` por tenant, ADR-7): la misma carrera entre el chequeo previo de
            // `ServicioDeUsuarios` y el `SaveChangesAsync` puede chocar acá.
            { SqlState: "23505", ConstraintName: "ux_usuarios_usuario" } =>
                (StatusCodes.Status409Conflict, "El usuario ya existe.", "usuario_duplicado"),

            // stage-5-pos-ventas (Slice 3, task 3.10, db-error-backstops, design: Backstop Map
            // — "ordering trap"): ux_comprobantes_venta_numero tiene que resolverse ANTES de
            // llegar a ClasificarUnicidad (el caso genérico de más abajo) — su nombre contiene
            // "_numero", así que la rama genérica de esa substring (backstop de
            // ux_clientes_numero, "Ya existe un cliente con ese número") lo atraparía primero y
            // lo clasificaría mal. Exact-match acá, ARRIBA del caso genérico, mismo criterio
            // que el trap "_codigo_interno"/"codigos_barra" vs. "_codigo" dentro de esa
            // función — la diferencia es que ese trap se resuelve DENTRO de ClasificarUnicidad
            // (mismo Contains-chain) y este tiene que resolverse ANTES de siquiera llamarla.
            { SqlState: "23505", ConstraintName: string uxComprobante }
                when string.Equals(uxComprobante, "ux_comprobantes_venta_numero", StringComparison.OrdinalIgnoreCase) =>
                (StatusCodes.Status409Conflict, "Ya existe un comprobante con ese número en este punto de venta y tipo.", "numero_de_comprobante_duplicado"),

            // stage-8-compras-transferencias-inventario (Slice 1, task 1.10, db-error-backstops,
            // design: Backstop Map — "ordering trap"): mismo tratamiento que
            // ux_comprobantes_venta_numero de arriba — su nombre contiene "_numero", así que
            // tiene que resolverse por nombre EXACTO acá, ANTES de ClasificarUnicidad (que lo
            // clasificaría como "numero_duplicado", el mensaje de ux_clientes_numero).
            { SqlState: "23505", ConstraintName: string uxCompra }
                when string.Equals(uxCompra, "ux_comprobantes_compra_numero_externo", StringComparison.OrdinalIgnoreCase) =>
                (StatusCodes.Status409Conflict, "Ya existe una compra con ese número de comprobante para este proveedor y tipo.", "compra_duplicada"),

            // stage-8-compras-transferencias-inventario (Slice 1, task 1.10, db-error-backstops,
            // design: Backstop Map): orden es server-asignado dentro del replace-set (Slice 2) —
            // exención documentada de prueba de carrera, misma familia que
            // ux_items_comprobante_venta_orden.
            { SqlState: "23505", ConstraintName: string uxOrdenCompra }
                when string.Equals(uxOrdenCompra, "ux_items_comprobante_compra_orden", StringComparison.OrdinalIgnoreCase) =>
                (StatusCodes.Status409Conflict, "Ya existe un ítem con ese orden en esta compra.", "orden_de_item_duplicado"),

            // stage-16-ordenes-de-compra (Slice 1, task 1.19, db-error-backstops, design decisión
            // 10-11): ux_ordenes_compra_numero tiene que resolverse por nombre EXACTO, ANTES de
            // ClasificarUnicidad — su nombre contiene "_numero", así que la rama genérica de más
            // abajo (la familia de ux_clientes_numero) lo atraparía primero y lo clasificaría
            // como "numero_duplicado". TERCERA ocurrencia del ordering trap, mismo tratamiento
            // exacto que ux_comprobantes_venta_numero (:127-129) y
            // ux_comprobantes_compra_numero_externo (:136-138) — bajo operación normal esta rama
            // es inalcanzable (el único escritor es AsignadorDeNumeroComprobante, cuya atomicidad
            // ya está probada por la etapa 5/8), queda como backstop de esquema puro, probado por
            // un INSERT crudo out-of-band (slice 1) y por la concurrencia real de dos `enviar`
            // simultáneos en un mismo punto de venta (slice 2).
            { SqlState: "23505", ConstraintName: string uxOrdenCompraNumero }
                when string.Equals(uxOrdenCompraNumero, "ux_ordenes_compra_numero", StringComparison.OrdinalIgnoreCase) =>
                (StatusCodes.Status409Conflict, "Ya existe una orden de compra con ese número en este punto de venta.", "numero_de_orden_duplicado"),

            // stage-16-ordenes-de-compra (Slice 1, task 1.19, db-error-backstops): orden es
            // server-asignado dentro del replace-set del borrador (slice 2) — exención
            // documentada de prueba de carrera, misma familia que
            // ux_items_comprobante_compra_orden de arriba.
            { SqlState: "23505", ConstraintName: string uxItemOrdenCompra }
                when string.Equals(uxItemOrdenCompra, "ux_items_orden_compra_orden", StringComparison.OrdinalIgnoreCase) =>
                (StatusCodes.Status409Conflict, "Ya existe un ítem con ese orden en esta orden de compra.", "orden_de_item_duplicado"),

            // stage-12-lotes-vencimientos (Slice 1, task 1.16, db-error-backstops, design
            // decisión 5): ux_lotes_articulo_codigo tiene que resolverse por nombre EXACTO,
            // ANTES de llegar a ClasificarUnicidad (el caso genérico de más abajo) — su nombre
            // contiene la substring "_codigo" (la rama genérica "_codigo" de ClasificarUnicidad
            // lo atraparía primero y lo clasificaría como "codigo_duplicado", el mensaje
            // genérico), mismo "ordering trap" que ux_comprobantes_venta_numero/
            // ux_comprobantes_compra_numero_externo de arriba. La carrera es real: get-or-create
            // (slice 3, ServicioDeLotes.ResolverOCrearAsync) usa un INSERT ... ON CONFLICT DO
            // UPDATE que resuelve la carrera normal sin tocar nunca esta rama; esta rama es el
            // backstop de una escritura cruda/fuera de banda o de un `POST /api/stock/lotes`
            // (slice 3) concurrente con el mismo código.
            { SqlState: "23505", ConstraintName: string uxLote }
                when string.Equals(uxLote, "ux_lotes_articulo_codigo", StringComparison.OrdinalIgnoreCase) =>
                (StatusCodes.Status409Conflict, "Ya existe un lote con ese código para este artículo.", "lote_duplicado"),

            // stage-12-lotes-vencimientos (Slice 1, task 1.16, design decisión 5): exención
            // documentada de prueba de carrera, mismo patrón que pk_stock/
            // pk_numeraciones_comprobante — ningún camino de escritura de esta slice ni de las
            // siguientes hace un INSERT crudo contra este índice: el lote sin identificar se
            // crea siempre a través de ux_lotes_articulo_codigo primero (design decisión 5, el
            // código reservado SIN-IDENTIFICAR serializa la creación en ESE índice), así que
            // ux_lotes_sin_identificar nunca puede chocar por el camino de servicio. Defensa de
            // esquema pura, alcanzable solo por un INSERT crudo/fuera de banda, probada con SQL
            // directo (task 1.21).
            { SqlState: "23505", ConstraintName: string uxSinIdentificar }
                when string.Equals(uxSinIdentificar, "ux_lotes_sin_identificar", StringComparison.OrdinalIgnoreCase) =>
                (StatusCodes.Status409Conflict, "Ya existe un lote sin identificar para este artículo.", "lote_sin_identificar_duplicado"),

            // stage-17-presupuestos-y-remitos (Slice 1, task 1.21, db-error-backstops, design
            // decisión 18, proposal §J): ux_presupuestos_numero tiene que resolverse por nombre
            // EXACTO, ANTES de ClasificarUnicidad — su nombre contiene "_numero", así que la
            // rama genérica de más abajo lo atraparía primero y lo clasificaría como
            // "numero_duplicado" (el mensaje de ux_clientes_numero). CUARTA ocurrencia del
            // ordering trap, mismo tratamiento exacto que ux_ordenes_compra_numero (:159-161) —
            // bajo operación normal esta rama es inalcanzable (el único escritor es
            // AsignadorDeNumeroComprobante con la serie 'PRES'), queda como backstop de esquema
            // puro, probado por un INSERT crudo out-of-band (slice 1) y por la concurrencia real
            // de dos `enviar` simultáneos en un mismo punto de venta (slice 2).
            { SqlState: "23505", ConstraintName: string uxPresupuestoNumero }
                when string.Equals(uxPresupuestoNumero, "ux_presupuestos_numero", StringComparison.OrdinalIgnoreCase) =>
                (StatusCodes.Status409Conflict, "Ya existe un presupuesto con ese número en este punto de venta.", "numero_de_presupuesto_duplicado"),

            // stage-17-presupuestos-y-remitos (Slice 1, task 1.22, db-error-backstops, design
            // decisión 5/proposal §G): ux_comprobantes_venta_presupuesto_origen tiene que
            // resolverse por nombre EXACTO, ANTES de ClasificarUnicidad — a diferencia de la
            // rama de arriba, ESTA sí es alcanzable por un cliente real (idPresupuestoOrigen
            // viaja en el body): el UPDATE guardado de EscriturasDePresupuesto.MarcarConvertidoAsync
            // (slice 3) es la autoridad primaria de la carrera de conversión, este índice
            // parcial es el backstop de esquema que la serializa de verdad. Probado con un
            // INSERT crudo (slice 1) y con la carrera real de dos conversiones concurrentes
            // (slice 3).
            { SqlState: "23505", ConstraintName: string uxPresupuestoOrigen }
                when string.Equals(uxPresupuestoOrigen, "ux_comprobantes_venta_presupuesto_origen", StringComparison.OrdinalIgnoreCase) =>
                (StatusCodes.Status409Conflict, "Este presupuesto ya fue convertido en una venta.", "presupuesto_ya_convertido"),

            // stage-17-presupuestos-y-remitos (Slice 1, task 1.23, db-error-backstops): orden es
            // server-asignado dentro del replace-set del borrador (slice 2) — exención
            // documentada de prueba de carrera, misma familia que
            // ux_items_orden_compra_orden/ux_items_comprobante_compra_orden.
            { SqlState: "23505", ConstraintName: string uxItemPresupuesto }
                when string.Equals(uxItemPresupuesto, "ux_items_presupuesto_orden", StringComparison.OrdinalIgnoreCase) =>
                (StatusCodes.Status409Conflict, "Ya existe un ítem con ese orden en este presupuesto.", "orden_de_item_duplicado"),

            // stage-17-presupuestos-y-remitos (Slice 4, task 4.22, db-error-backstops, design
            // decisión 18, proposal §J): ux_remitos_numero tiene que resolverse por nombre
            // EXACTO, ANTES de ClasificarUnicidad — su nombre contiene "_numero", así que la
            // rama genérica de más abajo lo atraparía primero y lo clasificaría como
            // "numero_duplicado" (el mensaje de ux_clientes_numero). QUINTA ocurrencia del
            // ordering trap, mismo tratamiento exacto que ux_presupuestos_numero (:209-211) —
            // bajo operación normal esta rama es inalcanzable (el único escritor es
            // AsignadorDeNumeroComprobante con la serie 'REM'), queda como backstop de esquema
            // puro, probado por un INSERT crudo out-of-band (slice 4) y por la concurrencia real
            // de dos `emitir` simultáneos en un mismo punto de venta (slice 5).
            { SqlState: "23505", ConstraintName: string uxRemitoNumero }
                when string.Equals(uxRemitoNumero, "ux_remitos_numero", StringComparison.OrdinalIgnoreCase) =>
                (StatusCodes.Status409Conflict, "Ya existe un remito con ese número en este punto de venta.", "numero_de_remito_duplicado"),

            // stage-17-presupuestos-y-remitos (Slice 4, task 4.23, db-error-backstops): orden es
            // server-asignado dentro del replace-set del borrador (slice 5) — exención
            // documentada de prueba de carrera, misma familia que
            // ux_items_presupuesto_orden/ux_items_orden_compra_orden.
            { SqlState: "23505", ConstraintName: string uxItemRemito }
                when string.Equals(uxItemRemito, "ux_items_remito_orden", StringComparison.OrdinalIgnoreCase) =>
                (StatusCodes.Status409Conflict, "Ya existe un ítem con ese orden en este remito.", "orden_de_item_duplicado"),

            // Backstop genérico (judgment-day, slice 3 ronda 1) para las ~10 unicidades nuevas
            // de catálogos/parámetros/catálogos fiscales: mismo mecanismo de carrera que los
            // dos casos de arriba, pero agrupado por familia (a partir del nombre del índice,
            // que ya codifica qué se duplicó) en vez de repetir un caso por índice.
            { SqlState: "23505", ConstraintName: string ux }
                when ClasificarUnicidad(ux) is { } familia =>
                (StatusCodes.Status409Conflict, familia.Titulo, familia.Codigo),

            // Backstop de defensa en profundidad (judgment-day ronda 1, item 3, stage-3-slice-2):
            // el .Distinct() del servicio ya evita el duplicado en el camino normal, pero esta
            // es la constraint real ante cualquier duplicado que lo esquive (p.ej. una carrera
            // entre dos PUT concurrentes sobre el mismo artículo). PK_articulos_empresas usa la
            // convención por default de EF (PascalCase, "PK_"), a diferencia del resto del
            // esquema (snake_case, doc 10) — match case-insensitive para no depender de esa
            // inconsistencia de nombre.
            { SqlState: "23505", ConstraintName: string pk }
                when string.Equals(pk, "pk_articulos_empresas", StringComparison.OrdinalIgnoreCase) =>
                (StatusCodes.Status409Conflict, "La empresa ya está en el subconjunto de disponibilidad del artículo.", "empresa_duplicada_en_subset"),

            // Backstop de la constraint que cierra la baja irreversible del Consumidor Final
            // (stage-2-clientes-proveedores, design decision 4, task 1.12): ReglaDeClientes.
            // ValidarNoConsumidorFinal ya bloquea el camino normal de ServicioDeClientes —
            // esto es el backstop ante un UPDATE/DELETE que la esquive directamente.
            { SqlState: "23514", ConstraintName: "ck_clientes_cf_protegido" } =>
                (StatusCodes.Status409Conflict, "El cliente Consumidor Final no se puede editar ni eliminar.", "consumidor_final_protegido"),

            // Backstop de esquema (judgment-day, slice 3 ronda 2, item 2; GATE-APROBADO
            // 2026-08-03) para "vigente_hasta > vigente_desde" en precios — ServicioDePrecios.
            // AbrirNuevoPrecioAsync ya lo garantiza en el camino de servicio (mismo código de
            // dominio, ver el chequeo simétrico contra la fila activa/el predecesor); esto cubre
            // una escritura cruda/fuera de banda que lo bypasee (misma familia que
            // ck_clientes_cf_protegido).
            { SqlState: "23514", ConstraintName: "ck_precios_ventana_valida" } =>
                (StatusCodes.Status400BadRequest, "vigente_hasta no puede ser anterior a vigente_desde.", "vigente_desde_invalido"),

            // Backstop genérico para las FKs compuestas nuevas (fk_*_empresa, fk_categorias_padre,
            // fk_parametros_punto_venta, …): una referencia a una fila que no existe (o que
            // pertenece a otro tenant, invisible bajo RLS) llega acá como 23503 en vez de
            // dejar pasar un 500 — p.ej. un IdCategoriaPadre de otro tenant.
            //
            // El match por prefijo también atrapa FKs administradas por la plataforma (no solo
            // las alimentadas por input de cliente) — tradeoff deliberado (judgment-day, slice 3
            // ronda 2): preferimos convertir un eventual 500 no logueado en un 400 logueado
            // (ver el LogWarning de abajo) antes que mantener una lista cerrada de FKs que hay
            // que actualizar a mano en cada migración nueva. Cubre, sin cambio de código,
            // TODAS las FKs `fk_*` de cada etapa nueva del esquema (articulos, ofertas,
            // pos-ventas, turnos-caja, cuenta-corriente, compras-transferencias-inventario,
            // lotes-vencimientos, …) — el match es por convención de nombre, no por lista cerrada.
            { SqlState: "23503", ConstraintName: string fk } when fk.StartsWith("fk_", StringComparison.Ordinal) =>
                LogYClasificarReferenciaInvalida(fk, log),

            // stage-5-pos-ventas (Slice 3, task 3.11, db-error-backstops, design: Backstop Map):
            // pk_stock — exención documentada de prueba de carrera, misma familia que
            // pk_numeraciones_comprobante: el único escritor de stock (Slice 4/5, INSERT ...
            // ON CONFLICT DO UPDATE) nunca puede disparar 23505 por construcción. Defensa de
            // esquema pura, alcanzable solo por un INSERT crudo/fuera de banda, probada con SQL
            // directo.
            { SqlState: "23505", ConstraintName: string pkStock }
                when string.Equals(pkStock, "pk_stock", StringComparison.OrdinalIgnoreCase) =>
                (StatusCodes.Status409Conflict, "Ya existe stock cargado para ese artículo en ese punto de venta.", "stock_duplicado"),

            // stage-5-pos-ventas (Slice 3, task 3.12, db-error-backstops, design: Backstop Map):
            // las cuatro CHECKs de comprobantes_venta/pagos_comprobante/movimientos_stock
            // no comparten un prefijo común (a diferencia de "ck_ofertas_"), así que el guard
            // de esta rama llama directo a ClasificarCheckDeVentas (switch por nombre EXACTO,
            // nunca Contains) en vez de filtrar por StartsWith primero. ValidadorDePagos/
            // ReglaDeComprobantes/el camino de escritura de movimientos_stock (Slice 4/5) ya
            // validan los cuatro invariantes en el servicio — bajo operación normal ninguna de
            // las cuatro ramas es alcanzable, quedan como backstop de una escritura cruda/fuera
            // de banda (misma familia que ClasificarCheckDeOfertas).
            { SqlState: "23514", ConstraintName: string ckVenta }
                when ClasificarCheckDeVentas(ckVenta) is { } checkVenta =>
                (checkVenta.EstadoHttp, checkVenta.Titulo, checkVenta.Codigo),

            // stage-6-turnos-caja (Slice 1, task 1.7, db-error-backstops, design: Backstop Map):
            // switch por nombre EXACTO de las seis CHECKs nuevas de turnos_caja/movimientos_caja/
            // gastos/movimientos_tesoreria — sin prefijo compartido entre las cuatro tablas
            // (mismo motivo que ClasificarCheckDeVentas). ReglaDeTurnos/ReglaDeMovimientosDeCaja
            // (Slice 2), ServicioDeGastos (Slice 3) y ServicioDeTurnos.CerrarAsync (Slice 4) ya
            // validan estos invariantes en el camino de servicio — bajo operación normal ninguna
            // de las seis ramas es alcanzable, quedan como backstop de una escritura cruda/fuera
            // de banda.
            { SqlState: "23514", ConstraintName: string ckCaja }
                when ClasificarCheckDeCaja(ckCaja) is { } checkCaja =>
                (checkCaja.EstadoHttp, checkCaja.Titulo, checkCaja.Codigo),

            // stage-8-compras-transferencias-inventario (Slice 1, task 1.10, db-error-backstops,
            // design: Backstop Map): switch por nombre EXACTO detrás de un guard de prefijo
            // "ck_comprobantes_compra_"/"ck_items_comprobante_compra_" — mismo criterio que
            // ClasificarCheckDeOfertas ("ck_ofertas_"), no el de ClasificarCheckDeVentas/
            // ClasificarCheckDeCaja (sin prefijo compartido). CalculadorDeCompra/ServicioDeCompras
            // (Slice 2) ya validan estos cinco invariantes en el camino de servicio — bajo
            // operación normal ninguna rama es alcanzable, quedan como backstop de una escritura
            // cruda/fuera de banda.
            { SqlState: "23514", ConstraintName: string ckCompra }
                when (ckCompra.StartsWith("ck_comprobantes_compra_", StringComparison.Ordinal)
                        || ckCompra.StartsWith("ck_items_comprobante_compra_", StringComparison.Ordinal))
                    && ClasificarCheckDeCompras(ckCompra) is { } checkCompra =>
                (checkCompra.EstadoHttp, checkCompra.Titulo, checkCompra.Codigo),

            // stage-16-ordenes-de-compra (Slice 1, task 1.19, db-error-backstops, design
            // decisión 10, proposal §E): switch por nombre EXACTO detrás de un guard de prefijo
            // "ck_ordenes_compra_"/"ck_items_orden_compra_" — mismo criterio que ckCompra de
            // arriba. Las dos CHECKs de ordenes_compra son server-derivadas (ninguna llega desde
            // input de cliente); las dos de items_orden_compra sí reciben input de cliente
            // (cantidad/costo) y ya se validan en el servicio antes de escribir (Slice 2) — bajo
            // operación normal ninguna de las cuatro ramas es alcanzable, quedan como backstop
            // de una escritura cruda/fuera de banda, cada una probada con SQL directo (slice 1).
            { SqlState: "23514", ConstraintName: string ckOrdenCompra }
                when (ckOrdenCompra.StartsWith("ck_ordenes_compra_", StringComparison.Ordinal)
                        || ckOrdenCompra.StartsWith("ck_items_orden_compra_", StringComparison.Ordinal))
                    && ClasificarCheckDeOrdenesDeCompra(ckOrdenCompra) is { } checkOrdenCompra =>
                (checkOrdenCompra.EstadoHttp, checkOrdenCompra.Titulo, checkOrdenCompra.Codigo),

            // stage-17-presupuestos-y-remitos (Slice 1, tasks 1.24-1.25, db-error-backstops,
            // design decisión 18, proposal §J): switch por nombre EXACTO detrás de un guard de
            // prefijo "ck_presupuestos_"/"ck_items_presupuesto_" — mismo criterio que ckOrdenCompra
            // de arriba. ServicioDePresupuestos (slice 2) ya valida los dos invariantes en el
            // camino de servicio antes de escribir — bajo operación normal ninguna de las dos
            // ramas es alcanzable, quedan como backstop de una escritura cruda/fuera de banda,
            // cada una probada con SQL directo (slice 1).
            { SqlState: "23514", ConstraintName: string ckPresupuesto }
                when (ckPresupuesto.StartsWith("ck_presupuestos_", StringComparison.Ordinal)
                        || ckPresupuesto.StartsWith("ck_items_presupuesto_", StringComparison.Ordinal))
                    && ClasificarCheckDePresupuestos(ckPresupuesto) is { } checkPresupuesto =>
                (checkPresupuesto.EstadoHttp, checkPresupuesto.Titulo, checkPresupuesto.Codigo),

            // stage-17-presupuestos-y-remitos (Slice 4, tasks 4.24-4.26, db-error-backstops,
            // design decisión 18/Backstop Map, proposal §J): switch por nombre EXACTO detrás de
            // un guard de prefijo "ck_remitos_"/"ck_items_remito_" — mismo criterio que
            // ckPresupuesto de arriba. CINCO ramas (no tres): proposal §J agrupa las CHECKs 2/5/6/7
            // (cantidad Y costo) en una sola fila "exact-name 23514 mapping ... one test each", y
            // design.md's Backstop Map lista explícitamente CHECK 6/7 (costo) con la misma
            // mapping — el conteo de "3" de la Orchestrator Decision 9 de este archivo es un
            // artefacto de redacción (registrado como desvío, no una omisión): reconciliado a 5
            // para que el total del proposal (7 = 2 slice 1 + 5 slice 4) cierre. Todas
            // alcanzables solo por escritura cruda/fuera de banda — ServicioDeRemitos (slice 5)
            // ya valida cantidad en el camino de servicio antes de escribir; costo es
            // server-derivado, ningún input de cliente lo dispara.
            { SqlState: "23514", ConstraintName: string ckRemito }
                when (ckRemito.StartsWith("ck_remitos_", StringComparison.Ordinal)
                        || ckRemito.StartsWith("ck_items_remito_", StringComparison.Ordinal))
                    && ClasificarCheckDeRemitos(ckRemito) is { } checkRemito =>
                (checkRemito.EstadoHttp, checkRemito.Titulo, checkRemito.Codigo),

            // Backstop genérico (db-error-backstops, judgment-day slice 3 ronda 1): cualquier
            // valor numérico que desborda la precisión/escala de su columna (p.ej. un margen o
            // un límite de crédito por encima de lo que valida la capa de servicio) llega acá
            // como 22003 en vez de dejar pasar un 500 — no está atado a una constraint puntual
            // porque numeric_value_out_of_range aplica por igual a cualquier columna numeric(p,s).
            { SqlState: "22003" } =>
                (StatusCodes.Status400BadRequest, "El valor numérico está fuera de rango.", "valor_fuera_de_rango"),

            // stage-4-ofertas (Slice 1, task 1.7, db-error-backstops, design decision 8):
            // switch por nombre EXACTO detrás de un guard de prefijo "ck_ofertas_" — a
            // diferencia de ClasificarUnicidad (match por Contains/sufijo), acá no hay riesgo
            // de colisión de substring porque los cuatro nombres son literales completos y
            // mutuamente exclusivos. Agregado DESPUÉS de las dos ramas exactas existentes
            // (ck_clientes_cf_protegido/ck_precios_ventana_valida): cero cambio de
            // comportamiento para esas dos. ReglaDeOfertas ya valida los cuatro invariantes en
            // el camino de servicio (Slice 1, dominio; Slice 2, ServicioDeOfertas) — bajo
            // operación normal ninguna de las cuatro ramas de abajo es alcanzable, quedan como
            // backstop de una escritura cruda/fuera de banda (misma familia que las dos ramas
            // de arriba).
            { SqlState: "23514", ConstraintName: string ckOferta }
                when ckOferta.StartsWith("ck_ofertas_", StringComparison.Ordinal)
                    && ClasificarCheckDeOfertas(ckOferta) is { } checkOferta =>
                (checkOferta.EstadoHttp, checkOferta.Titulo, checkOferta.Codigo),

            // stage-4-ofertas (Slice 1, task 1.7): pk_ofertas_listas — la única superficie
            // genuinamente racy de esta etapa (design: Backstop Map). El replace-set de
            // ServicioDeOfertas (Slice 2: delete-all + insert transaccional, ids .Distinct()ed)
            // ya evita el duplicado en el camino normal; esto cubre dos PUT concurrentes que
            // reemplazan el mismo set de listas de una oferta y chocan acá — misma familia que
            // pk_articulos_empresas.
            { SqlState: "23505", ConstraintName: string pkOfertasListas }
                when string.Equals(pkOfertasListas, "pk_ofertas_listas", StringComparison.OrdinalIgnoreCase) =>
                (StatusCodes.Status409Conflict, "La lista de precios ya está en el subconjunto de targeting de la oferta.", "oferta_lista_duplicada"),

            // stage-5-pos-ventas (Slice 2, task 2.6, db-error-backstops, design: Backstop Map):
            // pk_numeraciones_comprobante — exención documentada de prueba de carrera. A
            // diferencia de pk_ofertas_listas/PK_articulos_empresas (que SÍ tienen un camino de
            // escritura normal que puede chocar), el único escritor de esta tabla
            // (AsignadorDeNumeroComprobante) inserta con ON CONFLICT DO NOTHING — nunca puede
            // disparar 23505 por construcción. Esta rama queda como defensa de esquema pura,
            // alcanzable solo por un INSERT crudo/fuera de banda que bypasee el asignador
            // (misma familia que pk_stock, Slice 3), probada con SQL directo, no con una
            // carrera real.
            { SqlState: "23505", ConstraintName: string pkNumeracion }
                when string.Equals(pkNumeracion, "pk_numeraciones_comprobante", StringComparison.OrdinalIgnoreCase) =>
                (StatusCodes.Status409Conflict, "Ya existe una numeración para ese punto de venta y tipo de comprobante.", "numeracion_duplicada"),

            _ => null
        };

    /// <summary>Agrupa los índices únicos nuevos por familia a partir del sufijo de su
    /// nombre — evita repetir un caso por índice para <c>ux_areas_nombre_*</c>,
    /// <c>ux_marcas_nombre_*</c>, <c>ux_grupos_nombre_*</c>, <c>ux_medios_pago_nombre_*</c>,
    /// <c>ux_categorias_nombre_*</c>, <c>ux_alicuotas_iva_nombre</c>,
    /// <c>ux_condiciones_fiscales_codigo</c>, <c>ux_tipos_comprobante_codigo</c> y las dos de
    /// <c>parametros</c>. <c>ux_parametros_*</c> se resuelve antes que el resto porque no
    /// sigue el patrón "_nombre"/"_codigo".
    ///
    /// El match por sufijo es deliberadamente amplio a todo el esquema (judgment-day, slice 3
    /// ronda 2): no se restringe a una lista cerrada de índices, así que también cubre
    /// unicidades preexistentes solo de seed (p.ej. <c>ux_roles_nombre</c>) de forma inocua,
    /// porque esos índices nunca reciben un INSERT/UPDATE desde un camino de escritura de
    /// cliente. La familia <c>codigo_duplicado</c> de catálogos fiscales (alícuotas de IVA,
    /// condiciones fiscales, tipos de comprobante) hoy no tiene camino de escritura de cliente
    /// (son de solo lectura para un tenant), así que queda exenta de la prueba de carrera
    /// exigida por el punto de decisión del skill `db-error-backstops` — solo aplica el mapeo
    /// 23505, sin race test, hasta que exista un endpoint de alta/edición.</summary>
    private static (string Codigo, string Titulo)? ClasificarUnicidad(string nombreDeIndice)
    {
        if (nombreDeIndice is "ux_parametros_empresa" or "ux_parametros_punto_venta")
        {
            return ("parametro_duplicado", "Ya existe un parámetro con esa clave en este alcance.");
        }

        // stage-6-turnos-caja (Slice 1, task 1.7, db-error-backstops, design: Backstop Map):
        // ux_turnos_caja_abierto — backstop de "One Open Turno Per Punto De Venta" (spec:
        // turnos-de-caja). Sin trampa de ordering: el nombre no contiene ninguna de las
        // substrings que matchean más abajo (_cuit/_numero/_nombre/_codigo/_vigente/_default),
        // así que su posición acá no importa — se deja arriba, junto al otro literal exacto,
        // por prolijidad. La prueba de carrera real vive en Slice 2 (task 2.8); acá solo el
        // proof raw-SQL 23505 (task 1.9).
        if (nombreDeIndice == "ux_turnos_caja_abierto")
        {
            return ("turno_ya_abierto", "Ya existe un turno abierto en este punto de venta.");
        }

        // stage-6-turnos-caja (Slice 1, task 1.7, db-error-backstops, design: Backstop Map):
        // ux_arqueos_turno_medio — exención documentada de prueba de carrera: el cierre deriva
        // el set de filas dentro de su propio lock exclusivo (Slice 4), así que el camino normal
        // nunca puede chocar acá. Mismo motivo de "sin trampa de ordering" que el caso de arriba.
        if (nombreDeIndice == "ux_arqueos_turno_medio")
        {
            return ("arqueo_duplicado", "Ya existe un arqueo para ese medio de pago en este turno.");
        }

        // stage-2-clientes-proveedores (task 1.12, backstop map): ux_proveedores_cuit —
        // spec "cuit Uniqueness Is Scoped Per Tenant". La exención de la prueba de carrera
        // (documentada en Slice 1/batch 4) cierra en la Slice 3: ServicioDeProveedores.
        // CrearAsync ya existe y ExigirCuitDisponibleAsync es un pre-chequeo best-effort, no
        // el backstop real — dos altas concurrentes con el mismo cuit pueden pasar las dos ese
        // chequeo y competir recién acá, en el SaveChangesAsync de la que pierde. La prueba de
        // carrera vive en ProveedoresEndpointsTests (task 3.5): a diferencia de
        // ux_clientes_numero, cuit es un valor provisto por el cliente HTTP (no asignado por
        // un contador atómico), así que no hay ningún lock de fila que serialice la carrera
        // por construcción — dos POST concurrentes con Task.WhenAll alcanzan (mismo patrón sin
        // rendezvous forzado que CatalogosTests, no el de ParametrosTests: acá el alta es un
        // INSERT incondicional con pre-chequeo, no un upsert).
        if (nombreDeIndice.Contains("_cuit", StringComparison.Ordinal))
        {
            return ("cuit_duplicado", "Ya existe un proveedor con ese CUIT en este tenant.");
        }

        // stage-2-clientes-proveedores (task 1.12, backstop map): ux_clientes_numero —
        // backstop del contador atómico de AsignadorDeNumeroCliente (spec: Atomic Per-Tenant
        // Numero Assignment); nunca debería chocar acá bajo operación normal.
        //
        // Ojo: la prueba de atomicidad y la prueba de backstop son cosas DISTINTAS (corregido,
        // judgment-day ronda 1). ClientesEndpointsTests.LaCreacionConcurrenteAsignaNumerosSecuencialesSinExponerElBackstop
        // (Slice 2, task 2.5) prueba que dos altas concurrentes NUNCA disparan esta rama — el
        // lock de fila de `UPDATE ... RETURNING` sobre `numeraciones_clientes` ya serializa las
        // dos transacciones (mismo hallazgo que AsignadorDeNumeroClienteConcurrenciaTests,
        // Slice 1 batch 3), así que esa prueba demuestra la AUSENCIA de esta rama en operación
        // normal, no su backstop. El backstop en sí — que esta rama SÍ traduce el 23505 a 409
        // cuando algo bypassea el contador — lo prueba
        // BackstopClientesYProveedoresTests.UnaFilaConNumeroDuplicadoInsertadaPorFueraDelContadorViolaLaUnicidad
        // (INSERT crudo por SQL que fuerza el duplicado, sin pasar por el contador atómico).
        if (nombreDeIndice.Contains("_numero", StringComparison.Ordinal))
        {
            return ("numero_duplicado", "Ya existe un cliente con ese número en este tenant.");
        }

        if (nombreDeIndice.Contains("_nombre", StringComparison.Ordinal))
        {
            return ("nombre_duplicado", "Ya existe un registro con ese nombre en este alcance.");
        }

        // stage-3-articulos-y-precios (task 1.10, db-error-backstops, ordering care): las dos
        // ramas de acá ABAJO tienen que ir ANTES de la rama genérica "_codigo" — ambos nombres
        // de índice contienen esa substring ("ux_articulos_codigo_interno" arranca justo con
        // "_codigo_interno", "ux_codigos_barra_codigo_tenant" arranca con "_codigo" dentro de
        // "_codigos_barra") y el match por Contains es de arriba hacia abajo: sin este orden,
        // los dos caerían en silencio en la familia genérica codigo_duplicado en vez de su
        // propio código de dominio.
        //
        // stage-3-articulos-y-precios (Slice 2, task 2.8): la prueba de carrera de este
        // backstop ya no está exenta — ServicioDeArticulos.CrearAsync es el camino de
        // escritura real. A diferencia de `ux_clientes_numero` (contador atómico con lock de
        // fila) pero IGUAL que `ux_proveedores_cuit`, el valor autogenerado SÍ tiene un lock de
        // fila que serializa la carrera (numeraciones_articulos, design decision 6) — la
        // carrera GENUINA es la del `codigo_interno` provisto por el cliente HTTP (sin ningún
        // contador de por medio), probada en
        // ArticulosEndpointsTests.LaCreacionConcurrenteConElMismoCodigoInternoProvistoDaExactamenteUnGanador.
        if (nombreDeIndice.Contains("_codigo_interno", StringComparison.Ordinal))
        {
            return ("codigo_interno_duplicado", "Ya existe un artículo con ese código interno en este tenant.");
        }

        // stage-3-articulos-y-precios (Slice 2, task 2.9): la prueba de carrera de este
        // backstop ya no está exenta — ServicioDeArticulos.AgregarCodigoBarraAsync es el camino
        // de escritura real. `codigo` es siempre un valor provisto por el cliente HTTP, sin
        // contador ni lock de fila que serialice nada por construcción (misma familia que
        // `ux_proveedores_cuit`, no la de `ux_clientes_numero`), probada en
        // ArticulosEndpointsTests.LaCreacionConcurrenteConElMismoCodigoDeBarraDaExactamenteUnGanador.
        if (nombreDeIndice.Contains("codigos_barra", StringComparison.Ordinal))
        {
            return ("codigo_barra_duplicado", "Ya existe ese código de barras en este tenant.");
        }

        if (nombreDeIndice.Contains("_codigo", StringComparison.Ordinal))
        {
            return ("codigo_duplicado", "Ya existe un registro con ese código.");
        }

        // stage-3-articulos-y-precios (task 1.10): ux_precios_vigente — backstop de "at most
        // one pending future price" (design decisions 3/4). Sin colisión con ninguna otra
        // familia: "_vigente" no aparece en ningún otro nombre de índice del esquema.
        //
        // Slice 3 judgment-day ronda 1 (item 2), REEMPLAZA el comentario anterior sobre task
        // 3.11: ServicioDePrecios.AbrirNuevoPrecioAsync ahora toma un pg_advisory_xact_lock
        // determinístico por par (idArticulo, idListaPrecio) ANTES de leer nada de precios, así que
        // CUALQUIER escritura concurrente sobre el mismo par se serializa de verdad — el segundo
        // llamador espera el lock y, al retomarlo, ve el estado YA COMITEADO por el primero
        // (incluida la fila recién insertada si el primero fue el primer precio del par), y hace
        // un cierre-y-apertura legítimo en vez de chocar contra este índice. Por eso esta carrera
        // YA NO es alcanzable por el camino de servicio: el backstop sigue existiendo como
        // defensa de esquema, pero solo queda alcanzable por una escritura cruda/fuera de banda
        // que bypasee el servicio (misma familia que PK_articulos_empresas, Slice 2 judgment-day
        // ronda 2 — ver ArticulosEndpointsTests.UnaFilaDeSubsetDuplicadaInsertadaPorFueraDelServicioViolaLaPk).
        // La prueba HTTP de este par (antes "un ganador + un 409") se adaptó para probar la
        // serialización real en su lugar: PreciosEndpointsTests.
        // LaCreacionConcurrenteDeDosPrimerosPreciosSeSerializaYAmbosSuceden.
        if (nombreDeIndice.Contains("_vigente", StringComparison.Ordinal))
        {
            return ("precio_vigente_duplicado", "Ya existe un precio vigente para este artículo en esta lista.");
        }

        // ux_listas_precio_default_compartido/empresa (stage-2-clientes-proveedores, backstop
        // map): sembrado originalmente solo por provisioning/backfill (exento de race test en
        // esa etapa, sin camino de escritura de cliente). stage-3-articulos-y-precios (Slice 4,
        // task 4.1/db-error-backstops) cierra la exención: ServicioDeListasPrecio.
        // DesmarcarDefaultActualAsync es ahora el camino de escritura real del intercambio de
        // es_default — la carrera GENUINA (dos listas del mismo alcance compitiendo por
        // convertirse en la nueva default) queda probada en
        // ListasPrecioEndpointsTests.LaAsignacionConcurrenteDeEsDefaultAOtrasDosListasDaExactamenteUnGanador.
        if (nombreDeIndice.Contains("_default", StringComparison.Ordinal))
        {
            return ("default_duplicado", "Ya existe una lista de precios default en este alcance.");
        }

        return null;
    }

    /// <summary>Deja constancia en el log de qué FK disparó el backstop antes de traducirla a
    /// 400 (judgment-day, slice 3 ronda 2): como el match por prefijo también cubre FKs
    /// administradas por la plataforma, este warning es lo único que preserva observabilidad
    /// para ese caso — de otro modo pasaría de un 500 logueado a un 400 silencioso.</summary>
    private static (int EstadoHttp, string Titulo, string Codigo) LogYClasificarReferenciaInvalida(
        string nombreDeFk, ILogger log)
    {
        log.LogWarning("Referencia inválida por la restricción {NombreDeFk}.", nombreDeFk);
        return (StatusCodes.Status400BadRequest, "La referencia indicada no existe.", "referencia_invalida");
    }

    /// <summary>stage-4-ofertas (Slice 1, task 1.7, tasks.md "Orchestrator Decisions Recorded
    /// This Phase" #1): switch por nombre EXACTO de las cuatro CHECKs de <c>ofertas</c> — el
    /// código de las dos exclusividades sigue el código pineado por <c>specs/ofertas/spec.md</c>
    /// (<c>oferta_alcance_invalido</c>/<c>oferta_beneficio_invalido</c>, no el nombre borrador
    /// de <c>design.md</c>), las otras dos siguen el nombre de <c>design.md</c> tal cual
    /// (ningún escenario de spec las pinea distinto).</summary>
    private static (int EstadoHttp, string Titulo, string Codigo)? ClasificarCheckDeOfertas(string nombreDeCheck) =>
        nombreDeCheck switch
        {
            "ck_ofertas_alcance_exclusivo" =>
                (StatusCodes.Status400BadRequest,
                    "La oferta tiene que apuntar a exactamente un artículo, grupo o categoría.",
                    "oferta_alcance_invalido"),

            "ck_ofertas_beneficio_exclusivo" =>
                (StatusCodes.Status400BadRequest,
                    "La oferta tiene que definir exactamente un beneficio: precio unitario, porcentaje o importe fijo.",
                    "oferta_beneficio_invalido"),

            "ck_ofertas_ventana_valida" =>
                (StatusCodes.Status400BadRequest,
                    "La ventana de vigencia de la oferta es inválida.",
                    "ventana_de_oferta_invalida"),

            "ck_ofertas_dias_semana" =>
                (StatusCodes.Status400BadRequest,
                    "Los días de semana de la oferta tienen que ser valores de 1 a 7 sin repetir.",
                    "dias_semana_invalidos"),

            // Nombre inesperado detrás del guard de prefijo "ck_ofertas_" (p.ej. una CHECK nueva
            // agregada al esquema sin actualizar este switch): cae al mismo 500 genérico que
            // cualquier otro caso no mapeado (mismo patrón que ClasificarUnicidad — null en vez
            // de lanzar desde el exception handler).
            _ => null
        };

    /// <summary>stage-5-pos-ventas (Slice 3, task 3.12, tasks.md "Orchestrator Decisions
    /// Recorded This Phase" #2, design: Backstop Map; ampliado en un follow-up con
    /// <c>ck_pagos_comprobante_importe_no_negativo</c>, gate de DB aprobado 2026-08-04): switch
    /// por nombre EXACTO de las cuatro CHECKs nuevas de <c>comprobantes_venta</c>/
    /// <c>pagos_comprobante</c>/<c>movimientos_stock</c> — sin prefijo compartido entre las tres
    /// tablas (a diferencia de <c>ClasificarCheckDeOfertas</c>, que sí puede filtrar por
    /// <c>"ck_ofertas_"</c> antes de llamar), así que el caso del switch de arriba llama directo
    /// a esta función.
    /// <c>vuelto_de_pago_negativo</c> se pinea DISTINTO del código de dominio
    /// <c>vuelto_invalido</c> de <c>ValidadorDePagos</c> (regla <c>Σ vuelto &gt; max(0, Σ
    /// importe − total)</c>): son dos familias de rechazo distintas — reusar el mismo texto de
    /// código las confundiría en un log o en el cliente.
    /// <c>pago_importe_negativo</c>, en cambio, REUSA el código de dominio de la regla 0 de
    /// <c>ValidadorDePagos</c> a propósito: es la MISMA regla de negocio (un importe negativo no
    /// tiene significado), esta CHECK es solo su backstop de esquema — un cliente nunca debería
    /// distinguir si el rechazo vino de la validación de aplicación o de la CHECK.</summary>
    private static (int EstadoHttp, string Titulo, string Codigo)? ClasificarCheckDeVentas(string nombreDeCheck) =>
        nombreDeCheck switch
        {
            "ck_comprobantes_venta_numero_positivo" =>
                (StatusCodes.Status400BadRequest,
                    "El número del comprobante tiene que ser positivo.",
                    "numero_de_comprobante_invalido"),

            "ck_pagos_comprobante_vuelto_no_negativo" =>
                (StatusCodes.Status400BadRequest,
                    "El vuelto de un pago no puede ser negativo.",
                    "vuelto_de_pago_negativo"),

            "ck_pagos_comprobante_importe_no_negativo" =>
                (StatusCodes.Status400BadRequest,
                    "El importe de un pago no puede ser negativo.",
                    "pago_importe_negativo"),

            "ck_movimientos_stock_cantidad_no_cero" =>
                (StatusCodes.Status400BadRequest,
                    "El movimiento de stock tiene que tener una cantidad distinta de cero.",
                    "movimiento_de_stock_sin_cantidad"),

            "ck_items_comprobante_venta_costo_no_negativo" =>
                (StatusCodes.Status400BadRequest,
                    "El costo de un item de venta no puede ser negativo.",
                    "costo_de_item_invalido"),

            "ck_items_comprobante_venta_estimado_con_costo" =>
                (StatusCodes.Status400BadRequest,
                    "Un item marcado como costo estimado tiene que tener un costo cargado.",
                    "costo_estimado_sin_costo"),

            _ => null
        };

    /// <summary>stage-6-turnos-caja (Slice 1, task 1.7, tasks.md "Orchestrator Decisions
    /// Recorded This Phase" — design decisión 8): switch por nombre EXACTO de las seis CHECKs
    /// nuevas de <c>turnos_caja</c>/<c>movimientos_caja</c>/<c>gastos</c>/
    /// <c>movimientos_tesoreria</c>. <c>ck_movimientos_caja_motivo_minimo</c> y
    /// <c>ck_movimientos_caja_importe</c> cubren, cada una, una sola regla UNIFORME sobre los
    /// tres <c>tipo_movimiento_caja</c> (design decisión 8: sin rama por tipo) — la CHECK en sí
    /// no puede distinguir qué tipo la disparó, así que su código de backstop es genérico y
    /// DISTINTO de los dos códigos de dominio que sí distinguen por tipo
    /// (<c>movimiento_de_caja_sin_motivo</c> para retiro/refuerzo,
    /// <c>motivo_de_apertura_cajon_invalido</c> para apertura_cajon — ambos de
    /// <c>ReglaDeMovimientosDeCaja</c>, Slice 2): mismo criterio que
    /// <c>ck_pagos_comprobante_vuelto_no_negativo</c>, que tampoco reusa el código de una regla
    /// de dominio distinta.</summary>
    private static (int EstadoHttp, string Titulo, string Codigo)? ClasificarCheckDeCaja(string nombreDeCheck) =>
        nombreDeCheck switch
        {
            "ck_turnos_caja_fondo_inicial_no_negativo" =>
                (StatusCodes.Status400BadRequest,
                    "El fondo inicial no puede ser negativo.",
                    "fondo_inicial_negativo"),

            "ck_turnos_caja_cierre_consistente" =>
                (StatusCodes.Status400BadRequest,
                    "El turno quedó en un estado de cierre inconsistente.",
                    "turno_cierre_inconsistente"),

            "ck_movimientos_caja_importe" =>
                (StatusCodes.Status400BadRequest,
                    "El importe del movimiento de caja no es válido para ese tipo.",
                    "movimiento_de_caja_importe_invalido"),

            "ck_movimientos_caja_motivo_minimo" =>
                (StatusCodes.Status400BadRequest,
                    "El motivo del movimiento de caja tiene que tener al menos 5 caracteres.",
                    "movimiento_de_caja_motivo_invalido"),

            "ck_gastos_importe_positivo" =>
                (StatusCodes.Status400BadRequest,
                    "El importe del gasto tiene que ser positivo.",
                    "gasto_importe_invalido"),

            "ck_movimientos_tesoreria_cadena" =>
                (StatusCodes.Status400BadRequest,
                    "La cadena de tesorería es inconsistente (final tiene que ser inicio + ingreso − egreso).",
                    "tesoreria_cadena_invalida"),

            _ => null
        };

    /// <summary>stage-8-compras-transferencias-inventario (Slice 1, task 1.10, db-error-backstops,
    /// design: Backstop Map): switch por nombre EXACTO de las cinco CHECKs nuevas de
    /// <c>comprobantes_compra</c>/<c>items_comprobante_compra</c>, detrás del guard de prefijo
    /// del caso de arriba (el criterio de <c>ClasificarCheckDeOfertas</c>).</summary>
    private static (int EstadoHttp, string Titulo, string Codigo)? ClasificarCheckDeCompras(string nombreDeCheck) =>
        nombreDeCheck switch
        {
            "ck_comprobantes_compra_confirmada_completa" =>
                (StatusCodes.Status400BadRequest,
                    "La compra no puede confirmarse sin número de comprobante y fecha.",
                    "compra_incompleta_para_confirmar"),

            "ck_comprobantes_compra_totales_no_negativos" =>
                (StatusCodes.Status400BadRequest,
                    "Los totales de la compra no pueden ser negativos.",
                    "totales_de_compra_invalidos"),

            "ck_items_comprobante_compra_cantidad_positiva" =>
                (StatusCodes.Status400BadRequest,
                    "La cantidad de un ítem de compra tiene que ser positiva.",
                    "cantidad_de_item_invalida"),

            "ck_items_comprobante_compra_costo_no_negativo" =>
                (StatusCodes.Status400BadRequest,
                    "El costo unitario de un ítem de compra no puede ser negativo.",
                    "costo_de_item_invalido"),

            "ck_items_comprobante_compra_importes_no_negativos" =>
                (StatusCodes.Status400BadRequest,
                    "Los importes de un ítem de compra no pueden ser negativos.",
                    "importes_de_item_invalidos"),

            // stage-12-lotes-vencimientos (Slice 5, judgment-day, FIX 1b): backstop de
            // ck_items_comprobante_compra_lote_input — ServicioDeCompras.ValidarVencimientosDeRecepcion
            // (guard primario, FIX 1a) ya rechaza esto ANTES de tocar la base en el camino normal
            // (Crear/ActualizarBorradorAsync); esta rama queda como defensa de esquema pura ante
            // cualquier camino futuro que la esquive, mismo código de dominio
            // (lote_input_incompleto) para que el cliente nunca distinga cuál capa lo atajó.
            "ck_items_comprobante_compra_lote_input" =>
                (StatusCodes.Status400BadRequest,
                    "Un ítem con codigo_lote tiene que traer también fecha_vencimiento.",
                    "lote_input_incompleto"),

            _ => null
        };

    /// <summary>stage-16-ordenes-de-compra (Slice 1, task 1.19, design decisión 10, proposal §E):
    /// switch por nombre EXACTO de las cuatro CHECKs nuevas de <c>ordenes_compra</c>/
    /// <c>items_orden_compra</c>, detrás del guard de prefijo del caso de arriba. CHECK 1/CHECK 2
    /// son 409 (server-derivadas, ninguna entra por input de cliente — un rechazo de esquema acá
    /// solo puede significar una escritura cruda). CHECK 3/CHECK 4 son 400, misma familia y
    /// mismo status que <c>ck_items_comprobante_compra_cantidad_positiva</c>/
    /// <c>..._costo_no_negativo</c> en <see cref="ClasificarCheckDeCompras"/>: ambas reciben
    /// input real de cliente (cantidad pedida, costo estimado) y el servicio ya las valida
    /// primero con el mismo código de dominio — esta rama es solo el backstop de esquema.</summary>
    private static (int EstadoHttp, string Titulo, string Codigo)? ClasificarCheckDeOrdenesDeCompra(string nombreDeCheck) =>
        nombreDeCheck switch
        {
            "ck_ordenes_compra_envio_completo" =>
                (StatusCodes.Status409Conflict,
                    "El número y la fecha de envío de la orden de compra tienen que llegar juntos.",
                    "orden_compra_envio_incompleto"),

            "ck_ordenes_compra_cierre" =>
                (StatusCodes.Status409Conflict,
                    "El cierre de la orden de compra es inconsistente.",
                    "orden_compra_cierre_incoherente"),

            "ck_items_orden_compra_cantidad_positiva" =>
                (StatusCodes.Status400BadRequest,
                    "La cantidad pedida de un ítem de orden de compra tiene que ser positiva.",
                    "cantidad_pedida_invalida"),

            "ck_items_orden_compra_costo_no_negativo" =>
                (StatusCodes.Status400BadRequest,
                    "El costo estimado de un ítem de orden de compra no puede ser negativo.",
                    "costo_estimado_invalido"),

            _ => null
        };

    /// <summary>stage-17-presupuestos-y-remitos (Slice 1, tasks 1.24-1.25, design decisión 18,
    /// proposal §J): switch por nombre EXACTO de las dos CHECKs nuevas de
    /// <c>presupuestos</c>/<c>items_presupuesto</c>, detrás del guard de prefijo del caso de
    /// arriba. Las dos son 409/400 respectivamente por el mismo criterio que
    /// <see cref="ClasificarCheckDeOrdenesDeCompra"/>: <c>ck_presupuestos_envio_completo</c> es
    /// server-derivada (ningún input de cliente la dispara directo, un rechazo de esquema acá
    /// solo puede significar una escritura cruda) → 409; <c>ck_items_presupuesto_cantidad_positiva</c>
    /// recibe input real de cliente (cantidad de línea) y el servicio ya la valida primero con
    /// el mismo código de dominio (<c>cantidad_de_linea_invalida</c>) → 400, esta rama es solo
    /// el backstop de esquema.</summary>
    private static (int EstadoHttp, string Titulo, string Codigo)? ClasificarCheckDePresupuestos(string nombreDeCheck) =>
        nombreDeCheck switch
        {
            "ck_presupuestos_envio_completo" =>
                (StatusCodes.Status409Conflict,
                    "El número, la fecha de envío y el vencimiento del presupuesto tienen que llegar juntos.",
                    "presupuesto_envio_incompleto"),

            "ck_items_presupuesto_cantidad_positiva" =>
                (StatusCodes.Status400BadRequest,
                    "La cantidad de una línea de presupuesto tiene que ser positiva.",
                    "cantidad_de_linea_invalida"),

            _ => null
        };

    /// <summary>stage-17-presupuestos-y-remitos (Slice 4, tasks 4.24-4.26, design decisión 18,
    /// proposal §J): switch por nombre EXACTO de las cinco CHECKs nuevas de
    /// <c>remitos</c>/<c>items_remito</c>, detrás del guard de prefijo del caso de arriba.
    /// <c>ck_remitos_salida_completa</c>/<c>ck_remitos_facturacion</c> son server-derivadas
    /// (ningún input de cliente las dispara directo) → 409, mismo criterio que
    /// <c>ck_presupuestos_envio_completo</c>. <c>ck_items_remito_cantidad_positiva</c> recibe
    /// input real de cliente y el servicio ya la valida primero con el mismo código de dominio
    /// (<c>cantidad_de_linea_invalida</c>) → 400. <c>ck_items_remito_costo_no_negativo</c>/
    /// <c>ck_items_remito_estimado_con_costo</c> son server-derivadas (el costo se congela al
    /// emitir, slice 5) → 400, mismo criterio que
    /// <see cref="ClasificarCheckDeVentas"/>'s CHECKs de costo.</summary>
    private static (int EstadoHttp, string Titulo, string Codigo)? ClasificarCheckDeRemitos(string nombreDeCheck) =>
        nombreDeCheck switch
        {
            "ck_remitos_salida_completa" =>
                (StatusCodes.Status409Conflict,
                    "El número y la fecha de salida del remito tienen que llegar juntos.",
                    "remito_salida_incompleta"),

            "ck_remitos_facturacion" =>
                (StatusCodes.Status409Conflict,
                    "El estado facturado y la factura ligada del remito tienen que llegar juntos.",
                    "remito_facturacion_incoherente"),

            "ck_items_remito_cantidad_positiva" =>
                (StatusCodes.Status400BadRequest,
                    "La cantidad de una línea de remito tiene que ser positiva.",
                    "cantidad_de_linea_invalida"),

            "ck_items_remito_costo_no_negativo" =>
                (StatusCodes.Status400BadRequest,
                    "El costo de una línea de remito no puede ser negativo.",
                    "costo_de_linea_invalido"),

            "ck_items_remito_estimado_con_costo" =>
                (StatusCodes.Status400BadRequest,
                    "Una línea de remito marcada como costo estimado tiene que tener un costo.",
                    "costo_estimado_invalido"),

            _ => null
        };
}
