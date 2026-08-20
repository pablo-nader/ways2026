using System.Data;
using System.Data.Common;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Storage;
using Ways.Application.Abstracciones;
using Ways.Application.Caja;
using Ways.Application.CuentaCorriente;
using Ways.Application.Exportacion;
using Ways.Application.Ofertas;
using Ways.Application.Stock;
using Ways.Domain.Articulos;
using Ways.Domain.Catalogos;
using Ways.Domain.Clientes;
using Ways.Domain.Common;
using Ways.Domain.CuentaCorriente;
using Ways.Domain.Organizacion;
using Ways.Domain.Stock;
using Ways.Domain.Ventas;

namespace Ways.Application.Ventas;

/// <summary>
/// Checkout del POS — la transacción más crítica del proyecto (design: Technical Approach,
/// "decide, then commit"). <see cref="EmitirAsync"/> tiene dos mitades bien separadas:
///
/// <list type="number">
/// <item>TODO lo que decide algo (precios, ofertas, parámetros, validación de pagos) corre
/// ANTES de abrir la transacción, como lecturas + reglas puras, y arma un
/// <see cref="PlanDeVenta"/> inmutable. Si esta mitad corriera DENTRO de la lambda reintentable
/// de <c>CreateExecutionStrategy</c>, un reintento podría recalcular un total DISTINTO del que
/// la mezcla de pagos ya validó.</item>
/// <item>La numeración se reserva y comitea en su PROPIA transacción, ANTES de la que escribe el
/// resto (corrección de esta slice: el número queda consumido aunque lo de abajo falle, ver el
/// comentario de <see cref="EmitirAsync"/>). Esa segunda transacción recibe el plan ya congelado
/// más el número ya comprometido, y solo escribe, en el orden pineado por design (comprobante →
/// items → pagos → stock ascendente por <c>id_articulo</c> → cuenta corriente). Cada fila mutable
/// se escribe con un único statement atómico que toma su propio row lock y devuelve el
/// post-estado (<c>UPDATE ... RETURNING</c>/<c>INSERT ... ON CONFLICT DO UPDATE ... RETURNING</c>)
/// — design decisión 1: sin advisory locks, el lock de fila del propio upsert alcanza.</item>
/// </list>
///
/// Dedicado (design decisión 10), no una extensión de ningún ABM: autorización, transacción y
/// forma de las consultas son todas propias de esta operación.
/// </summary>
public class ServicioDeVentas(
    IWaysDbContext db, IRelojDelSistema reloj, IContextoDeUsuario contexto, ServicioDeOfertas servicioDeOfertas,
    ServicioDeTurnos servicioDeTurnos, ServicioDeLotes servicioDeLotes)
{
    public async Task<ComprobanteEmitido> EmitirAsync(SolicitudDeVenta solicitud, CancellationToken ct = default)
    {
        var idTenant = ExigirTenantDeLaSesion();

        // design decisión 11 (forward obligation de Slice 3): id_empleado SIEMPRE sale del
        // actor autenticado — SolicitudDeVenta no tiene ningún campo de empleado que pueda
        // pisar esto.
        var idEmpleado = contexto.UsuarioId;

        // stage-17-presupuestos-y-remitos, Slice 3 (design: Transactions — ":59"): con
        // idPresupuestoOrigen, lineas tiene que llegar vacío/ausente (400 lineas_no_admitidas,
        // dto-contract-honesty regla 1) — el valor REAL (los items del presupuesto) se resuelve
        // más abajo, en la rama del snapshot (p6), reasignando esta misma variable.
        var lineas = solicitud.IdPresupuestoOrigen is null
            ? ExigirLineasValidas(solicitud.Lineas)
            : ExigirSinLineas(solicitud.Lineas);
        var pagos = solicitud.Pagos ?? [];

        // Pineado UNA sola vez acá — nunca se vuelve a leer dentro de la lambda reintentable
        // (design: The Sale Transaction, "momento := reloj.Ahora (pinned; never re-read on
        // retry)").
        var momento = reloj.Ahora;

        var tipo = await ResolverTipoComprobanteAsync(solicitud.CodigoTipoComprobante, ct);
        var puntoVenta = await ResolverPuntoVentaAsync(solicitud.IdPuntoVenta, ct);

        // design decisión 11 (Slice 5): turno SIEMPRE resuelto server-side, inmediatamente
        // después del punto de venta — un punto de venta apócrifo ya dio el 404 de ADR-8 arriba,
        // así que esto es lo primero que puede rechazar con 409 turno_no_abierto, ANTES de
        // cualquier consulta de precio/oferta (spec: Selling with no open turno fails before any
        // pricing work).
        var turno = await servicioDeTurnos.ResolverTurnoAbiertoAsync(puntoVenta.Id, ct);

        // stage-17-presupuestos-y-remitos, Slice 3 (design: Transactions — "RAMA DEL SNAPSHOT",
        // decisión 2): corre SOLO con idPresupuestoOrigen. p1-p6 del design, en orden. Reemplaza,
        // para esta venta, el precio "mostrado por el carrito" por una decisión de servidor
        // ANTERIOR (la del presupuesto), nunca por lo que el request diga — la autoridad de
        // precio sigue siendo el servidor (fact 1 de design: Technical Approach).
        Presupuesto? presupuestoOrigen = null;
        DateOnly? hoyEnZonaDelPuntoVenta = null;
        IReadOnlyList<ItemPresupuesto>? itemsPresupuestoOrigen = null;

        // judgment-day slice-3 ronda 2 (juez A, MAJOR): resolución de cliente CONDICIONAL — con
        // idPresupuestoOrigen presente, el cliente correcto lo resuelve la rama del snapshot
        // (p4/p6, desde presupuesto.IdCliente, comparando contra solicitud.IdCliente SIN resolver
        // primero) inmediatamente abajo. Resolverlo acá también, incondicional, era (a) una query
        // EF desperdiciada en TODA conversión exitosa (el resultado se pisaba con la asignación de
        // la rama) y (b) devolvía 404 "no existe el cliente" para un idCliente inexistente en la
        // solicitud, en vez del 400 cliente_no_coincide que p4 exige (p4 compara ids crudos, nunca
        // resueltos). ValidarComprobanteAsociado (más abajo) queda DESPUÉS de esta resolución a
        // propósito — es el primer uso de cliente.Id aguas abajo (PlanDeVenta lo vuelve a leer
        // después), y necesita el valor ya definitivo de cualquiera de las dos ramas.
        Cliente cliente;

        if (solicitud.IdPresupuestoOrigen is { } idPresupuestoOrigen)
        {
            (presupuestoOrigen, itemsPresupuestoOrigen, hoyEnZonaDelPuntoVenta, cliente) =
                await ResolverConversionDesdePresupuestoAsync(
                    idPresupuestoOrigen, puntoVenta.Id, tipo.Signo, solicitud.IdCliente, momento, ct);

            // p6: lineas := items del presupuesto (IdLote null: FEFO se resuelve abajo, sin
            // cambios) — reemplaza el resultado vacío de ExigirSinLineas de arriba.
            lineas = itemsPresupuestoOrigen
                .Select(i => new LineaDeVenta(i.IdArticulo, i.Cantidad, CodigoBarra: null))
                .ToList();
        }
        else
        {
            cliente = await ResolverClienteAsync(solicitud.IdCliente, ct);
        }

        var asociado = solicitud.IdComprobanteAsociado is { } idAsociado
            ? await db.ComprobantesVenta.FirstOrDefaultAsync(c => c.Id == idAsociado, ct)
            : null;

        ReglaDeComprobantes.ValidarComprobanteAsociado(
            tipo.Signo, solicitud.IdComprobanteAsociado, asociado, puntoVenta.Id, cliente.Id);

        // 7 consultas (ServicioDeOfertas.ResolverAsync, design: Technical Approach) — la
        // autoridad de precio ÚNICA, nunca lo que mostró el carrito (design decisión 3). Con
        // idPresupuestoOrigen, la resolución YA está congelada en items_presupuesto — este
        // llamado se salta entero (decisión 2: "resolucion ←→ sintetizada desde items_presupuesto").
        var lineasDeResolucion = lineas
            .Select(l => new LineaDeResolucion(l.IdArticulo, puntoVenta.IdEmpresa, cliente.IdListaPrecio, l.Cantidad))
            .ToList();
        var resolucion = presupuestoOrigen is null
            ? await servicioDeOfertas.ResolverAsync(lineasDeResolucion, momento, ct)
            : null;

        // 2 consultas: snapshot de articulos + alicuotas (design: The Sale Transaction). Sin
        // segunda validación de existencia de articulo — ResolverAsync ya la hizo (400
        // referencia_invalida) antes de llegar acá; si un id no aparece en el diccionario es un
        // bug de esta clase, no un caso de negocio alcanzable.
        var idsArticulo = lineas.Select(l => l.IdArticulo).Distinct().ToList();
        var articuloPorId = await db.Articulos
            .Where(a => idsArticulo.Contains(a.Id))
            .ToDictionaryAsync(a => a.Id, ct);

        var idsAlicuota = articuloPorId.Values.Select(a => a.IdAlicuotaIva).Distinct().ToList();
        var porcentajePorAlicuota = await db.AlicuotasIva
            .Where(a => idsAlicuota.Contains(a.Id))
            .ToDictionaryAsync(a => a.Id, a => a.Porcentaje, ct);

        // stage-17-presupuestos-y-remitos, Slice 3 (design decisión 3, fact 2): con
        // idPresupuestoOrigen, MaterializarItems NO puede reusarse — lee IdAlicuotaIva/
        // porcentaje_iva de HOY, ambos parte de la promesa congelada. MaterializarItemsDesdePresupuesto
        // es un materializador PRIVADO SEPARADO, MaterializarItems (:1007 y siguientes) queda
        // literalmente intacto — los dos corren CalculadorDeTotales.Calcular como única
        // autoridad aritmética (tensión T2).
        var (items, totales) = presupuestoOrigen is null
            ? MaterializarItems(tipo.Signo, lineas, resolucion!, articuloPorId, porcentajePorAlicuota, cliente.IdListaPrecio)
            : MaterializarItemsDesdePresupuesto(itemsPresupuestoOrigen!, articuloPorId);

        // Aserción de fidelidad de totales (design decisión 4, mutation target 30, CONFLICT #4 —
        // presupuesto_inconsistente): los totales recomputados por CalculadorDeTotales sobre el
        // snapshot congelado tienen que coincidir EXACTO con el header ya guardado del
        // presupuesto — un raw UPDATE que desincroniza presupuestos.total de sus items nunca
        // produce una venta silenciosamente distinta, produce este 409.
        if (presupuestoOrigen is not null
            && (totales.Subtotal != presupuestoOrigen.Subtotal
                || totales.DescuentoTotal != presupuestoOrigen.DescuentoTotal
                || totales.Total != presupuestoOrigen.Total))
        {
            throw new ErrorDominio(
                "presupuesto_inconsistente",
                "El presupuesto está inconsistente con sus items; no se puede convertir.", 409);
        }

        // 1 consulta batcheada (stage-12, design decisión 2 / spec parametros-operativos: "A
        // Single Batched Query Resolves All Three Keys"): tolerancia_pago + vuelto_maximo +
        // lotes_habilitado, resueltas directo (sin el pre-chequeo de pertenencia de
        // ServicioDeParametros.ResolverAsync — puntoVenta ya se resolvió arriba, así que
        // idEmpresa ya es de confianza). Reemplaza las 2 consultas separadas de antes de esta
        // etapa — 17 → 16 round trips (task 2.7). `lotesHabilitado` alimenta el plan FEFO de
        // slice 7, inmediatamente abajo.
        var (toleranciaPago, vueltoMaximo, lotesHabilitado) =
            await ResolverParametrosDeVentaAsync(puntoVenta.IdEmpresa, puntoVenta.Id, ct);

        // stage-12 slice 7 (design: "Write site 1", decide phase) — decidir si hay línea
        // lote-efectiva es GRATIS: articuloPorId ya está cargado arriba (MaterializarItems),
        // lotesHabilitado ya resolvió en la query batcheada de arriba. Cero queries de sondeo.
        var lineasConLote = items
            .Select((item, indice) => (Item: item, Indice: indice))
            .Where(x => x.Item.EsProducto
                && ReglaDeLotes.ControlEfectivo(articuloPorId[x.Item.IdArticulo].ControlaLote, lotesHabilitado))
            .ToList();

        // dto-contract-honesty (WARNING 4 del judgment-day de esta slice): un idLote en una línea
        // SIN lote efectivo no tiene destino — antes se ignoraba en silencio. Un cliente que lo
        // manda ahí está en un error real (artículo no controla lote, o el módulo está apagado),
        // así que se rechaza con el mismo código que un idLote inválido, en vez de tragárselo.
        var indicesConLoteEfectivo = lineasConLote.Select(x => x.Indice).ToHashSet();
        for (var indice = 0; indice < lineas.Count; indice++)
        {
            if (!indicesConLoteEfectivo.Contains(indice) && lineas[indice].IdLote is not null)
            {
                throw new ErrorDominio(
                    "lote_invalido",
                    $"El artículo {lineas[indice].IdArticulo} no tiene lote efectivo; no admite idLote.",
                    400);
            }
        }

        if (lineasConLote.Count > 0)   // ← la ÚNICA query nueva del camino caliente: 16 → 17 (spec
                                        // lotes-y-vencimientos: "Module on with a lot-controlled
                                        // articulo nets zero round-trip change")
        {
            var idsArticuloConLote = lineasConLote.Select(x => x.Item.IdArticulo).Distinct().ToList();
            var idsLotePedidos = lineasConLote
                .Select(x => lineas[x.Indice].IdLote)
                .Where(idLote => idLote is not null)
                .Select(idLote => idLote!.Value)
                .Distinct()
                .ToList();

            var saldos = await servicioDeLotes.LeerSaldosAsync(puntoVenta.Id, idsArticuloConLote, idsLotePedidos, ct);
            var saldosPorArticulo = saldos.ToLookup(s => s.IdArticulo);

            // Honestidad documental: "hoy" acá es UTC naive (interino por diseño, mismo criterio
            // que ServicioDeCompras.EjecutarConfirmarAsync / ServicioDeLotes.ListarAsync/CrearAsync
            // — slice 3), no la zona_horaria del PV. LoteVencido es un warning de decisión 12,
            // nunca un bloqueo.
            var hoy = DateOnly.FromDateTime(momento.UtcDateTime);
            var itemsResueltos = items.ToList();

            foreach (var (item, indice) in lineasConLote)
            {
                var saldosDelArticulo = saldosPorArticulo[item.IdArticulo].ToList();
                var idLotePedido = lineas[indice].IdLote;

                // stage-12 slice 9 (design: "NCX", decisión 8 del proposal): el default FEFO se
                // RECHAZA en una línea de nota de crédito (tipo.Signo < 0) — "el más viejo
                // primero" no significa nada para mercadería que VUELVE; el paquete físico
                // devuelto tiene su propia fecha impresa. La sugerencia del picker (task 9.2) le
                // da al operador un idLote candidato, pero el request tiene que traerlo explícito
                // — el lote sin-identificar sigue siendo una elección explícita válida (la válvula
                // de escape cuando no se puede identificar el paquete físico).
                if (idLotePedido is null && tipo.Signo < 0)
                {
                    throw new ErrorDominio(
                        "lote_requerido",
                        $"La línea de devolución del artículo {item.IdArticulo} requiere un idLote explícito.",
                        400);
                }

                SaldoDeLote loteResuelto;
                if (idLotePedido is { } idLote)
                {
                    // Un idLote provisto se valida contra `saldos` (existe, es del artículo, no
                    // borrado — el filtro global de EF sobre Lotes ya excluye soft-deleted, así
                    // que un id borrado simplemente no aparece acá) o se rechaza (spec
                    // lotes-y-vencimientos: "An invalid supplied idLote is rejected").
                    var posicion = saldosDelArticulo.FindIndex(s => s.IdLote == idLote);
                    if (posicion < 0)
                    {
                        throw new ErrorDominio(
                            "lote_invalido",
                            $"El lote {idLote} no existe, no pertenece al artículo {item.IdArticulo} o fue eliminado.",
                            400);
                    }

                    loteResuelto = saldosDelArticulo[posicion];
                }
                else if (ReglaDeLotes.ElegirFefo(saldosDelArticulo, hoy) is { } elegido)
                {
                    loteResuelto = elegido;
                }
                else
                {
                    // Ningún lote con saldo positivo (design decisión 7) — get-or-create perezoso
                    // del lote sin identificar, statement crudo, invisible al contador de
                    // presupuesto (misma familia que UpsertStockAsync).
                    var conexionParaLotes = await ObtenerConexionAbiertaAsync(ct);
                    var idSinIdentificar = await ServicioDeLotes.ResolverSinIdentificarAsync(
                        conexionParaLotes, transaccion: null, idTenant, item.IdArticulo, momento, ct);

                    loteResuelto = new SaldoDeLote(
                        item.IdArticulo, idSinIdentificar, ReglaDeLotes.CodigoSinIdentificar,
                        EsSinIdentificar: true, FechaVencimiento: null, Cantidad: 0m);
                }

                itemsResueltos[indice] = item with
                {
                    IdLote = loteResuelto.IdLote,
                    CodigoLote = loteResuelto.Codigo,
                    LoteVencido = ReglaDeLotes.EstaVencido(loteResuelto.FechaVencimiento, hoy)
                };
            }

            items = itemsResueltos;
        }

        // 1 consulta: medios de pago pedidos.
        var idsMedioPago = pagos.Select(p => p.IdMedioPago).Distinct().ToList();
        var medioPorId = await db.MediosPago
            .Where(m => idsMedioPago.Contains(m.Id))
            .ToDictionaryAsync(m => m.Id, ct);

        var idsMedioFaltantes = idsMedioPago.Except(medioPorId.Keys).ToList();
        if (idsMedioFaltantes.Count > 0)
        {
            throw new ErrorDominio("referencia_invalida", $"No existe el medio de pago {idsMedioFaltantes[0]}.", 400);
        }

        var pagosAValidar = pagos
            .Select(p =>
            {
                var medio = medioPorId[p.IdMedioPago];
                return new PagoAValidar(
                    p.IdMedioPago, medio.Comportamiento, medio.AdmiteVuelto, medio.RequiereReferencia,
                    p.Importe, p.Vuelto, p.Referencia);
            })
            .ToList();

        ValidadorDePagos.Validar(
            totales.Total, pagosAValidar, toleranciaPago, vueltoMaximo,
            cliente.EsConsumidorFinal, cliente.Saldo, cliente.LimiteCredito, cliente.CreditoIlimitado);

        var pagosDelPlan = pagos
            .Select(p => new PagoDelPlan(
                p.IdMedioPago, medioPorId[p.IdMedioPago].Comportamiento, p.Importe, p.Referencia, p.Vuelto))
            .ToList();

        var plan = new PlanDeVenta(
            idTenant, idEmpleado, tipo.Id, tipo.Codigo, momento, puntoVenta.Id, turno.Id, cliente.Id,
            solicitud.IdComprobanteAsociado, items, totales.Subtotal, totales.DescuentoTotal, totales.Total,
            pagosDelPlan, cliente.LimiteCredito, cliente.CreditoIlimitado,
            NormalizarOpcional(solicitud.DireccionEntrega), NormalizarOpcional(solicitud.Observaciones),
            presupuestoOrigen?.Id, hoyEnZonaDelPuntoVenta);

        // Corrección de esta slice al decisión 2 original (ver el doc-comment de
        // VentasAtomicidadYConcurrenciaTests): la numeración se reserva y COMITEA en su PROPIA
        // transacción, separada de la que escribe el resto de la venta — así "el número se
        // consume aunque falle el resto" (design: Failure Semantics) es literal, no una
        // aproximación que un ROLLBACK conjunto desmentía. Un commit ambiguo sobre ESTA
        // transacción es inofensivo: si en verdad comiteó, el contador ya avanzó; si el cliente
        // reintenta (execution strategy), reservar de nuevo solo vuelve a avanzarlo — nunca
        // duplica una fila (design decisión 2: "gaps are accepted", nunca duplicados).
        var estrategiaNumeracion = db.Database.CreateExecutionStrategy();
        var numero = await estrategiaNumeracion.ExecuteAsync(async () =>
            await AsignadorDeNumeroComprobante.AsignarComprometidoAsync(db, plan.IdTenant, plan.IdPuntoVenta, plan.CodigoTipoComprobante, ct));

        // ADR-16 (mismo trámite que ServicioDeAprovisionamiento/ServicioDeOfertas): la
        // transacción se abre ACÁ ADENTRO — EnableRetryOnFailure exige que la apertura viva
        // dentro de ExecuteAsync. El plan de arriba es el ÚNICO dato de negocio que cruza hacia
        // la lambda: cada entidad de EF se construye de cero en cada intento (retry contract).
        //
        // El número YA está comprometido cuando esta lambda arranca — un commit ambiguo acá (el
        // servidor comitea pero la conexión se corta antes del ACK al cliente) hace que
        // CreateExecutionStrategy reintente la lambda completa; sin la guarda de abajo, ese
        // reintento volvería a INSERTAR el mismo comprobante bajo el mismo número, violando
        // ux_comprobantes_venta_numero (o, peor, duplicando la venta si esa unicidad no
        // existiera). BuscarPorNumeroComprometidoAsync corre PRIMERO en cada intento: si el
        // commit anterior sí llegó a puerto, el comprobante ya existe y se devuelve tal cual en
        // vez de reinsertarse.
        var estrategia = db.Database.CreateExecutionStrategy();

        return await estrategia.ExecuteAsync(async () =>
            await BuscarPorNumeroComprometidoAsync(plan.IdPuntoVenta, plan.IdTipoComprobante, numero, ct)
            ?? await EjecutarTransaccionAsync(plan, numero, ct));
    }

    /// <summary>Detección de idempotencia (ver el comentario de <see cref="EmitirAsync"/> sobre
    /// el commit ambiguo): <c>null</c> ⇒ el número todavía no tiene comprobante, esta es la
    /// primera vez que se corre la mitad transaccional para él. Firma en primitivos (no recibe
    /// <see cref="PlanDeVenta"/> entero) a propósito: es la pieza que
    /// <c>VentasAtomicidadYConcurrenciaTests</c> ejercita por reflexión para probar la detección
    /// en aislamiento, sin tener que construir un <see cref="PlanDeVenta"/> completo (privado,
    /// sin constructor público) desde el test.</summary>
    private async Task<ComprobanteEmitido?> BuscarPorNumeroComprometidoAsync(
        int idPuntoVenta, int idTipoComprobante, long numero, CancellationToken ct)
    {
        var comprobante = await db.ComprobantesVenta.FirstOrDefaultAsync(
            c => c.IdPuntoVenta == idPuntoVenta && c.IdTipoComprobante == idTipoComprobante && c.Numero == numero, ct);

        if (comprobante is null)
        {
            return null;
        }

        var items = await db.ItemsComprobanteVenta
            .Where(i => i.IdComprobanteVenta == comprobante.Id)
            .OrderBy(i => i.Orden)
            .ToListAsync(ct);

        var pagos = await db.PagosComprobante
            .Where(p => p.IdComprobanteVenta == comprobante.Id)
            .ToListAsync(ct);

        return Proyectar(comprobante, items, pagos);
    }

    public async Task<ComprobanteEmitido> ObtenerAsync(int id, CancellationToken ct = default)
    {
        var comprobante = await BuscarComprobanteAsync(id, ct);

        var items = await db.ItemsComprobanteVenta
            .Where(i => i.IdComprobanteVenta == id)
            .OrderBy(i => i.Orden)
            .ToListAsync(ct);

        var pagos = await db.PagosComprobante
            .Where(p => p.IdComprobanteVenta == id)
            .ToListAsync(ct);

        return Proyectar(comprobante, items, pagos);
    }

    public async Task<PaginaDeVentas> ListarAsync(
        int? idPuntoVenta = null,
        DateTimeOffset? desde = null,
        DateTimeOffset? hasta = null,
        int? idCliente = null,
        EstadoComprobante? estado = null,
        int pagina = 1,
        int tamanio = 25,
        CancellationToken ct = default)
    {
        pagina = Math.Max(pagina, 1);
        tamanio = Math.Clamp(tamanio, 1, 200);

        var query = ConstruirQuery(idPuntoVenta, desde, hasta, idCliente, estado);

        var total = await query.CountAsync(ct);

        var crudos = await query
            .OrderByDescending(c => c.Fecha)
            .Skip((pagina - 1) * tamanio)
            .Take(tamanio)
            .Select(c => new { c.Id, c.Numero, c.Estado, c.Fecha, c.IdPuntoVenta, c.IdCliente, c.Total })
            .ToListAsync(ct);

        // NumeroDeComprobante.Formatear no traduce a SQL: se arma en memoria, después de traer
        // la página ya paginada/filtrada (nunca antes — evita materializar todo el listado).
        var items = crudos
            .Select(c => new ComprobanteListado(
                c.Id, c.Numero, NumeroDeComprobante.Formatear(c.IdPuntoVenta, c.Numero), c.Estado, c.Fecha,
                c.IdPuntoVenta, c.IdCliente, c.Total))
            .ToList();

        return new PaginaDeVentas(items, total, pagina, tamanio);
    }

    /// <summary>
    /// stage-11-exportacion-reportes (Slice 3, design decisión 7): mismo <see cref="ConstruirQuery"/>
    /// que <see cref="ListarAsync"/>, nunca un predicado redeclarado — <c>Contar → refuse →
    /// lectura única con <c>.Take(topeDeFilas + 1)</c></c>, jamás paginada. El segundo
    /// <see cref="GuardaDeTope.Exigir"/> es el backstop de carrera: si la lectura trae
    /// <c>topeDeFilas + 1</c> filas, el <c>COUNT(*)</c> de arriba quedó desactualizado (una fila
    /// se insertó entre las dos consultas) y esta exportación rechaza en vez de devolver un
    /// archivo truncado (mutation-proof-tests: "no truncated file can escape even in that
    /// window").
    /// </summary>
    public async Task<IReadOnlyList<ComprobanteListado>> ListarParaExportacionAsync(
        int? idPuntoVenta,
        DateTimeOffset? desde,
        DateTimeOffset? hasta,
        int? idCliente,
        EstadoComprobante? estado,
        int topeDeFilas,
        CancellationToken ct = default)
    {
        var query = ConstruirQuery(idPuntoVenta, desde, hasta, idCliente, estado);

        var cantidad = await query.CountAsync(ct);
        GuardaDeTope.Exigir(cantidad, topeDeFilas);

        var crudos = await query
            .OrderByDescending(c => c.Fecha)
            .Take(topeDeFilas + 1)
            .Select(c => new { c.Id, c.Numero, c.Estado, c.Fecha, c.IdPuntoVenta, c.IdCliente, c.Total })
            .ToListAsync(ct);

        GuardaDeTope.Exigir(crudos.Count, topeDeFilas);

        return crudos
            .Select(c => new ComprobanteListado(
                c.Id, c.Numero, NumeroDeComprobante.Formatear(c.IdPuntoVenta, c.Numero), c.Estado, c.Fecha,
                c.IdPuntoVenta, c.IdCliente, c.Total))
            .ToList();
    }

    /// <summary>Filtro compartido de <see cref="ListarAsync"/> y
    /// <see cref="ListarParaExportacionAsync"/> (design decisión 7): un solo lugar declara el
    /// predicado, nunca dos copias que puedan derivar.</summary>
    private IQueryable<ComprobanteVenta> ConstruirQuery(
        int? idPuntoVenta, DateTimeOffset? desde, DateTimeOffset? hasta, int? idCliente, EstadoComprobante? estado)
    {
        var query = db.ComprobantesVenta.AsQueryable();

        if (idPuntoVenta is { } pv)
        {
            query = query.Where(c => c.IdPuntoVenta == pv);
        }

        if (desde is { } d)
        {
            query = query.Where(c => c.Fecha >= d);
        }

        if (hasta is { } h)
        {
            query = query.Where(c => c.Fecha <= h);
        }

        if (idCliente is { } ic)
        {
            query = query.Where(c => c.IdCliente == ic);
        }

        if (estado is { } e)
        {
            query = query.Where(c => c.Estado == e);
        }

        return query;
    }

    // ---- Anulación (Slice 5, design: Protection Rules — "A comprobante is anulado at most
    // once"; spec: comprobantes-venta / Anulación Reverses Stock and CC, Never Restores by
    // Editing) --------------------------------------------------------------------------------

    /// <summary>Anula un comprobante emitido: revierte stock y cuenta corriente en LA MISMA
    /// transacción que la transición de estado — nunca un <c>restaurar</c> (doc 10 principio 6,
    /// ese endpoint no existe ni existirá). A diferencia de <see cref="EmitirAsync"/>, acá no hay
    /// nada que decidir por fuera de la transacción (ni precios, ni ofertas, ni validación de
    /// pagos): todo lo que esta operación escribe es la INVERSA exacta de lo que
    /// <see cref="EjecutarTransaccionAsync"/> ya escribió, tomada del ledger original
    /// (<c>movimientos_stock</c>/<c>movimientos_cuenta_corriente</c>), nunca recalculada desde
    /// <c>items_comprobante_venta</c> — así una línea de servicio (<c>EsProducto = false</c>, que
    /// nunca generó movimiento) automáticamente no genera tampoco su reversa, sin tener que
    /// re-consultar el catálogo.</summary>
    public async Task<ComprobanteEmitido> AnularAsync(int id, CancellationToken ct = default)
    {
        var idTenant = ExigirTenantDeLaSesion();
        var idEmpleado = contexto.UsuarioId;
        var momento = reloj.Ahora;

        // Sin reintento automático (ver el doc-comment de
        // FabricaDeEstrategiaSinReintento.CrearEstrategiaSinReintento): una anulación es humana y
        // manual, sin clave de idempotencia — un reintento de EnableRetryOnFailure sobre un
        // commit ambiguo re-correría el UPDATE condicional del paso 1, que ya no matchea
        // 'emitido' tras el commit real, y devolvería 409 comprobante_ya_anulado a una solicitud
        // que en verdad tuvo éxito. Un reintento manual del operador que vea ese 409 está viendo
        // información correcta: el comprobante ya está anulado.
        var estrategia = FabricaDeEstrategiaSinReintento.CrearEstrategiaSinReintento(db);
        return await estrategia.ExecuteAsync(async () => await EjecutarAnulacionAsync(id, idTenant, idEmpleado, momento, ct));
    }

    private async Task<ComprobanteEmitido> EjecutarAnulacionAsync(
        int id, int idTenant, int idEmpleado, DateTimeOffset momento, CancellationToken ct)
    {
        await using var transaccion = await db.Database.BeginTransactionAsync(ct);

        // Defensa en profundidad (design: Domain rules first, mismo criterio que
        // ValidarSignoDeLineas dentro de MaterializarItems): valida la transición contra el
        // estado pre-leído ANTES del UPDATE atómico del paso 1, que sigue siendo la única
        // autoridad race-safe — si otra anulación gana la carrera entre este SELECT y ese UPDATE,
        // la rama `idPuntoVentaAnulacion is null` de abajo la sigue atrapando sin depender de
        // este pre-chequeo.
        // AsNoTracking(): el UPDATE de abajo es SQL crudo, no pasa por el change tracker de EF —
        // sin esto, la entidad quedaría trackeada con el estado VIEJO y el ObtenerAsync() del
        // final devolvería ese mismo objeto stale desde el identity map, en vez de re-consultar.
        var comprobantePreLectura = await db.ComprobantesVenta.AsNoTracking().FirstOrDefaultAsync(c => c.Id == id, ct);
        if (comprobantePreLectura is not null)
        {
            ReglaDeComprobantes.ValidarTransicionAEstado(comprobantePreLectura.Estado, EstadoComprobante.Anulado);
        }

        var conexion = await ObtenerConexionAbiertaAsync(ct);
        var transaccionCruda = db.Database.CurrentTransaction?.GetDbTransaction();

        // 0. Guard del turno (design decisión 4; Slice 5 task 5.3): SELECT ... FOR SHARE OF t
        // ANTES del UPDATE atómico del paso 1 — un EXISTS embebido en el WHERE de ese UPDATE
        // leería el turno SIN lock, dejando pasar una anulación concurrente a un cierre que ya
        // derivó el arqueo. 0 filas (el comprobante tiene id_turno_caja NULL, era stage-5) deja
        // pasar sin lanzar (spec: Stage-5 NULL-turno comprobante stays anulable).
        await ExigirTurnoNoCerradoAsync(conexion, transaccionCruda, idTenant, id, ct);

        // 1. Transición atómica emitido → anulado — un único UPDATE ... WHERE estado = 'emitido'
        // RETURNING id_punto_venta (stage-14-auditoria-trazabilidad, Slice 3, design decisión 8 —
        // mismo criterio que ServicioDeTurnos.MarcarCerradoAsync). Sigue siendo la única autoridad
        // race-safe para "¿pasó la transición?", y ahora también contesta "¿en qué PV?" sin una
        // lectura extra. Dos anulaciones concurrentes del mismo comprobante se serializan acá:
        // Postgres toma el row lock de la primera, la segunda espera y, al retomarlo, re-evalúa
        // el WHERE contra el estado YA COMITEADO por la primera — 'anulado' no matchea 'emitido',
        // 0 filas, nunca un 500 ni una condición de carrera silenciosa.
        var resultadoAnulacion = await MarcarAnuladoAsync(conexion, transaccionCruda, idTenant, id, ct);

        if (resultadoAnulacion is null)
        {
            // ADR-8: mismo 404 para "no existe" y "es de otro tenant" (filtro de EF + RLS ya
            // deja invisible un comprobante ajeno) — solo si NINGUNA fila visible tiene ese id
            // es 404; si la fila existe pero ya estaba anulada, es 409 (spec: "idempotent-safe
            // against double-anulación").
            var existe = await db.ComprobantesVenta.AnyAsync(c => c.Id == id, ct);
            if (!existe)
            {
                throw ErrorDominio.NoEncontrado($"No existe el comprobante {id}.");
            }

            throw new ErrorDominio("comprobante_ya_anulado", "El comprobante ya está anulado.", 409);
        }

        var (idPuntoVentaAnulacion, codigoTipoAnulado) = resultadoAnulacion.Value;

        // stage-17-presupuestos-y-remitos, Slice 6 (design: Transactions — "ANULAR VENTA", decisión
        // 4/7, mutation targets 55/56/58): POSICIÓN 1.6 — inmediatamente DESPUÉS de MarcarAnuladoAsync
        // (paso 1) y ANTES de la auditoría (paso 1.5, preexistente) — nunca después del loop de stock
        // ni de cuenta corriente (design: "posición 2, nunca después de stock/clientes"). El `if` de
        // abajo es ESTRUCTURAL (mutation target 56): para una anulación ordinaria (`codigoTipoAnulado
        // != "TXR"`) esto es CERO statements extra — mismo criterio que la rama del snapshot de la
        // conversión (Slice 3, `if (plan.IdPresupuestoOrigen is { } ...)`). `codigoTipoAnulado` sale
        // del scalar subquery ensanchado de `MarcarAnuladoAsync` (mutation target 55) — nunca un
        // `SELECT` separado, que agregaría un statement extra a TODA anulación, no solo a la de un
        // TXR.
        if (codigoTipoAnulado == "TXR")
        {
            await EscriturasDeRemito.DesligarAsync(conexion, transaccionCruda, idTenant, id, momento, ct);
        }

        // 1.5. Auditoría (stage-14-auditoria-trazabilidad, Slice 3; spec auditoria-de-operaciones;
        // design call site 7) — MISMA transacción cruda, MISMA constante EstadoComprobante.Emitido
        // que ligó el WHERE del UPDATE de arriba, así que valor_anterior no puede divergir del
        // predicado que realmente lo autorizó. id_punto_venta sale del RETURNING recién leído,
        // nunca de comprobantePreLectura (AsNoTracking, tomado ANTES del UPDATE bajo otro filtro).
        // ServicioDeAuditoria se instancia local con los mismos db/reloj/contexto de este servicio
        // — el binding verify criterion de esta etapa restringe el diff de este archivo a las
        // líneas de EjecutarAnulacionAsync/MarcarAnuladoAsync, así que el constructor de
        // ServicioDeVentas (y VentasCheckoutTests, que lo instancia directo) queda intacto.
        var servicioDeAuditoriaAnulacion = new Ways.Application.Auditoria.ServicioDeAuditoria(db, reloj, contexto);
        var (valorAnteriorVentaAnulacion, valorNuevoVentaAnulacion) =
            Ways.Domain.Auditoria.PayloadDeAuditoria.AnulacionDeVenta(EstadoComprobante.Emitido, EstadoComprobante.Anulado);
        await servicioDeAuditoriaAnulacion.RegistrarAsync(
            conexion, transaccionCruda,
            new Ways.Domain.Auditoria.RegistroDeAuditoria(
                idTenant, idPuntoVentaAnulacion, Ways.Domain.Auditoria.AccionAuditada.VentaAnulacion, id,
                valorAnteriorVentaAnulacion, valorNuevoVentaAnulacion),
            ct);

        // 2. Movimientos de stock inversos — uno por cada movimiento ORIGINAL de motivo = venta
        // de este comprobante (nunca recalculado desde items: ver el doc-comment de
        // AnularAsync). Orden ascendente (id_articulo, id_lote), mismo criterio anti-deadlock que
        // EjecutarTransaccionAsync paso 5 (design decisión 2/8/9), aunque acá no hay otra venta
        // concurrente compitiendo por las mismas filas — se mantiene por consistencia de
        // convención, no por necesidad estricta. `id_lote` viaja YA en el movimiento original —
        // etapa 12, slice 8, task 8.3: la reversa lo copia estructuralmente (design: "Exactness is
        // structural, not derived"), sin re-derivar desde items_comprobante_venta ni volver a
        // resolver FEFO. El `ThenBy(IdLote)` de abajo es un IQueryable: el orden de los NULLs queda
        // en manos de la traducción SQL del provider (Postgres por default hace NULLS LAST en ASC),
        // no de una comparación en memoria — acá es convención, no defensivo, por eso no repite el
        // `ThenBy(HasValue).ThenBy(?? 0)` explícito del paso 5.
        var movimientosOriginales = await db.MovimientosStock
            .Where(m => m.IdComprobanteVenta == id && m.Motivo == MotivoStock.Venta)
            .OrderBy(m => m.IdArticulo)
            .ThenBy(m => m.IdLote)
            .ToListAsync(ct);

        foreach (var original in movimientosOriginales)
        {
            var inversa = -original.Cantidad;

            await InsertarMovimientoStockAsync(
                conexion, transaccionCruda, idTenant, original.IdArticulo, original.IdPuntoVenta, inversa,
                MotivoStock.Anulacion, id, idEmpleado, momento, original.IdLote, ct);

            await UpsertStockAsync(conexion, transaccionCruda, idTenant, original.IdArticulo, original.IdPuntoVenta, inversa, ct);

            if (original.IdLote is { } idLote)
            {
                await UpsertStockLoteAsync(
                    conexion, transaccionCruda, idTenant, original.IdArticulo, original.IdPuntoVenta, idLote, inversa, ct);
            }
        }

        // 3. Contramovimiento de cuenta corriente — uno por cada consumo/pago ORIGINAL de este
        // comprobante (spec: consumo-cuenta-corriente / Anulación Produces A Contramovimiento;
        // pagos-a-cuenta / Anulación Reverses The Pago Movement; Movimiento Schema At Rest: "tipo
        // = ajuste-shaped inverse rows used as the anulación contramovimiento", nunca tipo =
        // consumo/pago). stage-7-cuenta-corriente (Slice 2, task 2.6, design decisión 5 — el
        // widening de 3 líneas): el filtro se abre a Pago (una RC solo puede producir un
        // movimiento Pago, nunca un Consumo, así que un comprobante siempre cae en una sola de
        // las dos ramas de abajo) y -consumo.Importe/-movimiento.Importe restaura la deuda sin
        // rama de signo (un Pago ya es negativo, así que su reversa da positiva sola).
        var movimientosCcOriginales = await db.MovimientosCuentaCorriente
            .Where(m => m.IdComprobanteVenta == id
                && (m.Tipo == TipoMovimientoCc.Consumo || m.Tipo == TipoMovimientoCc.Pago))
            .ToListAsync(ct);

        // stage-7-cuenta-corriente (Slice 3, task 3.13, judgment-day slice-2 finding, judge A):
        // el guard de abajo NO puede confiar en IdMovimientoActualizacion tal como lo trajo el
        // ToListAsync de arriba — esa lectura corre SIN lock, así que una reliquidación
        // concurrente podría comitear su marcador justo ENTRE esa lectura y el commit de esta
        // anulación, produciendo un estado irrepresentable ("revertido y reliquidado" a la vez).
        // El lock del cliente cierra la ventana: ServicioDeReliquidacion toma el MISMO lock
        // (SELECT ... FOR UPDATE) como el PRIMER statement de su transacción — si esta anulación
        // lo toma primero, la reliquidación concurrente queda bloqueada hasta que esta transacción
        // termine; si la reliquidación ya lo tenía, esta anulación espera acá y retoma el lock
        // DESPUÉS de su commit, así que el re-chequeo de abajo, ya bajo el lock, ve el marcador
        // recién comiteado y falla cerrado con el mismo 409. Todos los movimientos de un mismo
        // comprobante comparten el mismo cliente (un comprobante tiene un único IdCliente), así
        // que un solo lock alcanza para todo el foreach — orden total sin cambios (turnos_caja →
        // clientes → ledger): el guard de turno (paso 0, arriba) ya corrió antes que este lock.
        if (movimientosCcOriginales.Count > 0)
        {
            await BloquearClienteAsync(conexion, transaccionCruda, idTenant, movimientosCcOriginales[0].IdCliente, ct);
        }

        foreach (var movimiento in movimientosCcOriginales)
        {
            // Re-chequeo BAJO el lock recién tomado — nunca el valor materializado por el
            // ToListAsync sin lock de arriba, que es justamente la lectura vulnerable al TOCTOU
            // que este método cierra (spec: pagos-a-cuenta / Anulación Reverses The Pago Movement
            // no lo pide, pero el consumo reliquidado sí — cierra el leak de anular un consumo ya
            // cubierto por una reliquidación, que dejaría el delta de ActualizacionPrecios en el
            // aire sin el Consumo que lo originó).
            var idMovimientoActualizacion = await LeerMarcadorDeReliquidacionAsync(
                conexion, transaccionCruda, idTenant, movimiento.Id, ct);
            if (idMovimientoActualizacion is not null)
            {
                throw new ErrorDominio(
                    "consumo_reliquidado", "El consumo ya fue reliquidado; no se puede anular.", 409);
            }

            // Un Consumo siempre trae id_pago_comprobante (EjecutarTransaccionAsync paso 6 lo
            // setea siempre); un Pago (RC) nunca lo trae (RegistrarPagoAsync lo inserta con
            // id_pago_comprobante NULL, design decisión 1) — invariante de escritura por tipo,
            // forzarlo acá es defensa en profundidad, no un caso de negocio alcanzable.
            var idPagoComprobante = movimiento.Tipo == TipoMovimientoCc.Consumo
                ? movimiento.IdPagoComprobante
                    ?? throw new InvalidOperationException(
                        $"El movimiento de consumo {movimiento.Id} no tiene id_pago_comprobante — invariante de escritura violado.")
                : (int?)null;

            var nuevoSaldo = await EscriturasDeCuentaCorriente.ActualizarSaldoClienteAsync(
                conexion, transaccionCruda, idTenant, movimiento.IdCliente, -movimiento.Importe, ct);

            await EscriturasDeCuentaCorriente.InsertarMovimientoCcAsync(
                conexion, transaccionCruda, idTenant, movimiento.IdCliente, momento, movimiento.IdPuntoVenta, idEmpleado,
                TipoMovimientoCc.Ajuste, id, idPagoComprobante, -movimiento.Importe, nuevoSaldo, detalle: null, ct);
        }

        await transaccion.CommitAsync(ct);

        return await ObtenerAsync(id, ct);
    }

    /// <summary>Guard de turno (design decisión 4; Slice 5 task 5.3) — <c>FOR SHARE OF t</c>
    /// contra el turno del comprobante, ANTES del UPDATE atómico de <see cref="MarcarAnuladoAsync"/>:
    /// mismo criterio que <c>ServicioDeTurnos.ExigirTurnoAbiertoBajoLockAsync</c>, un
    /// <c>FOR SHARE</c> propio en vez de un <c>EXISTS</c> sin lock embebido en el WHERE del
    /// UPDATE. 0 filas (el join no matchea — comprobante con <c>id_turno_caja NULL</c>) deja
    /// pasar sin lanzar; <c>'cerrado'</c> lanza <c>409 turno_cerrado</c>; <c>'abierto'</c> deja
    /// pasar.</summary>
    private static async Task ExigirTurnoNoCerradoAsync(
        DbConnection conexion, DbTransaction? transaccion, int idTenant, int idComprobanteVenta, CancellationToken ct)
    {
        await using var comando = conexion.CreateCommand();
        comando.Transaction = transaccion;
        comando.CommandText =
            "SELECT t.estado::text FROM turnos_caja t " +
            "JOIN comprobantes_venta c ON c.id_turno_caja = t.id_turno_caja AND c.id_tenant = t.id_tenant " +
            "WHERE c.id_comprobante_venta = $1 AND c.id_tenant = $2 " +
            "FOR SHARE OF t";

        ParametrosDeComando.Agregar(comando, idComprobanteVenta);
        ParametrosDeComando.Agregar(comando, idTenant);

        var estado = (string?)await comando.ExecuteScalarAsync(ct);
        if (estado == "cerrado")
        {
            throw new ErrorDominio("turno_cerrado", "El turno de este comprobante está cerrado.", 409);
        }
    }

    /// <summary>task 3.13: lock del cliente ANTES de re-chequear el marcador de reliquidación de
    /// cada movimiento — mismo criterio "lock primero, decide después" que design decisión 4
    /// (<c>ServicioDeReliquidacion</c>, paso 1, el mismo <c>SELECT ... FOR UPDATE</c>).</summary>
    private static async Task BloquearClienteAsync(
        DbConnection conexion, DbTransaction? transaccion, int idTenant, int idCliente, CancellationToken ct)
    {
        await using var comando = conexion.CreateCommand();
        comando.Transaction = transaccion;
        comando.CommandText = "SELECT 1 FROM clientes WHERE id_cliente = $1 AND id_tenant = $2 FOR UPDATE";

        ParametrosDeComando.Agregar(comando, idCliente);
        ParametrosDeComando.Agregar(comando, idTenant);

        await comando.ExecuteScalarAsync(ct);
    }

    /// <summary>task 3.13: re-lee <c>id_movimiento_actualizacion</c> directo de la base, bajo el
    /// lock del cliente ya tomado por <see cref="BloquearClienteAsync"/> — nunca el valor
    /// materializado por el <c>ToListAsync</c> sin lock de <see cref="EjecutarAnulacionAsync"/>,
    /// que es la lectura vulnerable al TOCTOU que este método cierra.</summary>
    private static async Task<int?> LeerMarcadorDeReliquidacionAsync(
        DbConnection conexion, DbTransaction? transaccion, int idTenant, int idMovimiento, CancellationToken ct)
    {
        await using var comando = conexion.CreateCommand();
        comando.Transaction = transaccion;
        comando.CommandText =
            "SELECT id_movimiento_actualizacion FROM movimientos_cuenta_corriente " +
            "WHERE id_movimiento = $1 AND id_tenant = $2";

        ParametrosDeComando.Agregar(comando, idMovimiento);
        ParametrosDeComando.Agregar(comando, idTenant);

        var resultado = await comando.ExecuteScalarAsync(ct);
        return resultado is null or DBNull ? null : Convert.ToInt32(resultado);
    }

    /// <summary>Único UPDATE atómico de la transición de estado — ver el doc-comment de
    /// <see cref="EjecutarAnulacionAsync"/> sobre por qué esto alcanza para serializar dos
    /// anulaciones concurrentes sin ningún lock explícito. stage-14-auditoria-trazabilidad, Slice
    /// 3 (design decisión 8, precedente exacto <c>ServicioDeTurnos.MarcarCerradoAsync</c>):
    /// <c>RETURNING id_punto_venta</c> en vez de <c>id_comprobante_venta</c> — el mismo lock que
    /// ya decide "¿pasó la transición?" contesta también "¿en qué PV?", sin una lectura extra ni
    /// confiar en el pre-read <c>AsNoTracking()</c>.
    ///
    /// stage-17-presupuestos-y-remitos, Slice 6 (design decisión 7, mutation target 55):
    /// <c>RETURNING</c> ensanchado con un SUBQUERY ESCALAR — <c>(SELECT t.codigo FROM
    /// tipos_comprobante t WHERE t.id_tipo_comprobante = comprobantes_venta.id_tipo_comprobante)</c>,
    /// nunca una columna nueva — el mismo <c>UPDATE</c> guardado que ya decide la transición
    /// también contesta "¿de qué tipo es?", sin un <c>SELECT</c> separado que agregaría un
    /// statement extra a TODA anulación (no solo a la de un <c>TXR</c>). El llamador usa
    /// <c>codigoTipo</c> para el guard estructural de la posición 1.6 (¿hay que desligar
    /// remitos?).</summary>
    private static async Task<(int IdPuntoVenta, string CodigoTipo)?> MarcarAnuladoAsync(
        DbConnection conexion, DbTransaction? transaccion, int idTenant, int idComprobanteVenta, CancellationToken ct)
    {
        await using var comando = conexion.CreateCommand();
        comando.Transaction = transaccion;
        comando.CommandText =
            "UPDATE comprobantes_venta SET estado = $1 " +
            "WHERE id_comprobante_venta = $2 AND id_tenant = $3 AND estado = $4 " +
            "RETURNING id_punto_venta, " +
            "(SELECT t.codigo FROM tipos_comprobante t WHERE t.id_tipo_comprobante = comprobantes_venta.id_tipo_comprobante)";

        ParametrosDeComando.Agregar(comando, EstadoComprobante.Anulado);
        ParametrosDeComando.Agregar(comando, idComprobanteVenta);
        ParametrosDeComando.Agregar(comando, idTenant);
        ParametrosDeComando.Agregar(comando, EstadoComprobante.Emitido);

        await using var lector = await comando.ExecuteReaderAsync(ct);
        if (!await lector.ReadAsync(ct))
        {
            return null;
        }

        return (lector.GetInt32(0), lector.GetString(1));
    }

    // ---- La transacción (design: The Sale Transaction, orden de statements pineado) ----------

    private async Task<ComprobanteEmitido> EjecutarTransaccionAsync(PlanDeVenta plan, long numero, CancellationToken ct)
    {
        await using var transaccion = await db.Database.BeginTransactionAsync(ct);

        // 0. Turno — re-chequeo bajo FOR SHARE, PRIMER statement (design decisiones 1 y 11;
        // Slice 5 task 5.2): el turno ya vino resuelto como abierto arriba, ANTES de esta
        // transacción — sin este re-chequeo, una venta concurrente a un cierre podría comitear
        // dentro de un turno cuyo arqueo YA se derivó. Reusa
        // ServicioDeTurnos.ExigirTurnoAbiertoBajoLockAsync tal cual (mismo criterio que
        // ServicioDeGastos.InsertarGastoAsync) — el IWaysDbContext es el mismo por scope de DI,
        // así que ve esta misma transacción recién abierta.
        await servicioDeTurnos.ExigirTurnoAbiertoBajoLockAsync(plan.IdTurnoCaja, ct);

        // stage-17-presupuestos-y-remitos, Slice 3 (design decisión 6): conexión/transacción
        // cruda obtenidas ACÁ — antes de esta slice se obtenían recién antes del paso 5 (stock);
        // el paso 1.5 de abajo las necesita ANTES del INSERT del comprobante. El cuerpo de los
        // pasos 5/6 más abajo queda byte-idéntico, solo reusa estas mismas variables en vez de
        // redeclararlas.
        var conexion = await ObtenerConexionAbiertaAsync(ct);
        var transaccionCruda = db.Database.CurrentTransaction?.GetDbTransaction();

        // 1.5. Conversión de presupuesto (design decisión 6) — POSICIÓN 1.5, entre el turno (paso
        // 0) y el INSERT del comprobante (paso 2). Honestidad documental (judgment-day slice-3,
        // juez B, targets 34/35 — re-documentado): la POSICIÓN no es lo que garantiza que el
        // perdedor de una carrera de conversión no escriba nada — el juez movió este bloque a
        // justo antes del COMMIT ("pre-commit") y la suite completa, incluida la carrera
        // convertir×convertir bajo interceptor, siguió en verde. Lo que realmente lo garantiza es
        // la ATOMICIDAD de la transacción (si el UPDATE guardado de abajo devuelve 0 filas, el
        // throw revierte TODO lo que esta transacción ya escribió, sin importar en qué línea esté
        // el throw) más el índice único parcial ux_comprobantes_venta_presupuesto_origen (backstop
        // de base de datos contra dos comprobantes ligados al mismo presupuesto). La posición 1.5
        // es FAIL-FAST DEFENSIVO — ahorra materializar items/stock/cuenta corriente y el tiempo de
        // lock que esas escrituras tomarían, para una conversión que de todos modos va a fallar —
        // nunca una cuestión de correctitud. ServicioDeVentasPosicionDeConversionTests.cs (source
        // de texto, mismo criterio que EscriturasDeOrdenDeCompraLockOrderTests) pinea esta posición
        // por su motivo real, sin afirmar una correctitud que la posición no otorga. Para una venta
        // común (idPresupuestoOrigen null), esto sigue siendo CERO statements extra — guard
        // ESTRUCTURAL del `if` de abajo (nunca el conteo de 16 consultas de ContadorDeComandos, que
        // es ciego a SQL crudo vía ExecuteScalarAsync: ver el doc-comment de
        // ServicioDeVentasConversionTests.UnaVentaComunSigueEmitiendoDieciseisConsultasConLaRamaDelSnapshotPresente
        // y ServicioDeVentasPosicionDeConversionTests.LaLlamadaAMarcarConvertidoAsyncNuncaOcurreFueraDelGuardNuloDeIdPresupuestoOrigen).
        if (plan.IdPresupuestoOrigen is { } idPresupuestoOrigenDelPlan)
        {
            var convertido = await EscriturasDePresupuesto.MarcarConvertidoAsync(
                conexion, transaccionCruda, plan.IdTenant, idPresupuestoOrigenDelPlan, plan.IdPuntoVenta,
                plan.HoyDelPuntoVenta!.Value, plan.Momento, ct);

            if (!convertido)
            {
                // 0 filas: reclasifica bajo FOR UPDATE en la causa precisa (404/409/400) — nunca
                // sigue de largo hacia el INSERT del comprobante.
                await EscriturasDePresupuesto.ExigirCausaDelRechazoAsync(
                    conexion, transaccionCruda, plan.IdTenant, idPresupuestoOrigenDelPlan, plan.IdPuntoVenta,
                    plan.HoyDelPuntoVenta!.Value, ct);
            }
        }

        // 1. Numeración — YA reservada y comprometida por AsignarNumeroComprometidoAsync, en su
        // propia transacción (ver el comentario de EmitirAsync). Esta transacción arranca
        // directo en el paso 2: el número que recibe como parámetro es un dato de solo lectura
        // acá, nunca se vuelve a pedir.
        //
        // 2. Comprobante.
        var comprobante = new ComprobanteVenta
        {
            IdTipoComprobante = plan.IdTipoComprobante,
            Numero = numero,
            Fecha = plan.Momento,
            IdPuntoVenta = plan.IdPuntoVenta,
            IdTurnoCaja = plan.IdTurnoCaja,
            IdEmpleado = plan.IdEmpleado,
            IdCliente = plan.IdCliente,
            IdComprobanteAsociado = plan.IdComprobanteAsociado,
            IdPresupuestoOrigen = plan.IdPresupuestoOrigen,
            Subtotal = plan.Subtotal,
            DescuentoTotal = plan.DescuentoTotal,
            Total = plan.Total,
            DireccionEntrega = plan.DireccionEntrega,
            Observaciones = plan.Observaciones,
            Estado = EstadoComprobante.Emitido,
            CreatedAt = plan.Momento,
            UpdatedAt = plan.Momento
        };
        db.ComprobantesVenta.Add(comprobante);
        await db.SaveChangesAsync(ct);

        // 3 + 4. Items y pagos, en un único SaveChanges (sin FK entre ellos, así que el orden
        // relativo entre las dos tablas no importa acá — solo necesitaban el Id del
        // comprobante, ya generado arriba).
        var orden = 1;
        var itemsEntidad = plan.Items
            .Select(i => new ItemComprobanteVenta
            {
                IdComprobanteVenta = comprobante.Id,
                Orden = orden++,
                IdArticulo = i.IdArticulo,
                Descripcion = i.Descripcion,
                CodigoBarra = i.CodigoBarra,
                IdArea = i.IdArea,
                IdListaPrecio = i.IdListaPrecio,
                IdOferta = i.IdOferta,
                IdAlicuotaIva = i.IdAlicuotaIva,
                PorcentajeIva = i.PorcentajeIva,
                Cantidad = i.Cantidad,
                PrecioUnitario = i.PrecioUnitario,
                Descuento = i.Descuento,
                Total = i.Total,
                CostoUnitario = i.CostoUnitario,
                // Etapa 12, slice 8, task 8.2 (design: "Item snapshot"): congelado desde el plan
                // FEFO ya resuelto en la fase de decisión (Slice 7) — nunca re-derivado, legal
                // bajo "Snapshot Immutability of Items" porque se agrega AL snapshot y no existe
                // endpoint de edición.
                IdLote = i.IdLote,
                CreatedAt = plan.Momento,
                UpdatedAt = plan.Momento
            })
            .ToList();
        db.ItemsComprobanteVenta.AddRange(itemsEntidad);

        var pagosEntidad = plan.Pagos
            .Select(p => new PagoComprobante
            {
                IdComprobanteVenta = comprobante.Id,
                IdMedioPago = p.IdMedioPago,
                Importe = p.Importe,
                Referencia = p.Referencia,
                Vuelto = p.Vuelto,
                CreatedAt = plan.Momento,
                UpdatedAt = plan.Momento
            })
            .ToList();
        db.PagosComprobante.AddRange(pagosEntidad);

        await db.SaveChangesAsync(ct);

        // 5. Stock — ORDEN ASCENDENTE por (id_articulo, id_lote NULLS FIRST) (design decisión 2/8/9,
        // no negociable): el upsert de abajo toma su propio row lock de forma implícita, así que
        // dos ventas que comparten artículos/lotes en orden distinto se deadlockearían sin este
        // orden total. `ThenBy(HasValue).ThenBy(?? 0)` en vez de `ThenBy(IdLote ?? 0)` a secas
        // (decisión 9): el segundo es una correctness claim que solo funciona porque las columnas
        // identity arrancan en 1; el primero declara la intención (NULLS FIRST) sin depender de la
        // configuración de una secuencia — es el mutation target de la task 8.7. Un artículo con
        // EsProducto = false es un servicio (doc 10 §3: "false = servicio: no toca stock") — se
        // salta ENTERO, ni movimiento ni upsert, en vez de escribir un movimiento sin sentido para
        // algo que nunca tuvo una fila en stock. El upsert agregado (`stock`, id_lote NULL) corre
        // SIEMPRE, primero; el upsert de `stock_lotes` solo cuando el item resolvió un lote.
        foreach (var item in plan.Items
                     .Where(i => i.EsProducto)
                     .OrderBy(i => i.IdArticulo)
                     .ThenBy(i => i.IdLote.HasValue)
                     .ThenBy(i => i.IdLote ?? 0))
        {
            var delta = -item.Cantidad;

            await InsertarMovimientoStockAsync(
                conexion, transaccionCruda, plan.IdTenant, item.IdArticulo, plan.IdPuntoVenta, delta,
                MotivoStock.Venta, comprobante.Id, plan.IdEmpleado, plan.Momento, item.IdLote, ct);

            await UpsertStockAsync(conexion, transaccionCruda, plan.IdTenant, item.IdArticulo, plan.IdPuntoVenta, delta, ct);

            if (item.IdLote is { } idLote)
            {
                await UpsertStockLoteAsync(
                    conexion, transaccionCruda, plan.IdTenant, item.IdArticulo, plan.IdPuntoVenta, idLote, delta, ct);
            }
        }

        // 6. Cuenta corriente — un pago por vez, en el orden pedido (raw ADO: un
        // `cliente.Saldo += x` trackeado por EF duplicaría el incremento en un reintento, ver
        // el Retry contract de design).
        for (var i = 0; i < plan.Pagos.Count; i++)
        {
            var pago = plan.Pagos[i];
            if (pago.Comportamiento != ComportamientoMedioPago.CuentaCorriente)
            {
                continue;
            }

            var nuevoSaldo = await EscriturasDeCuentaCorriente.ActualizarSaldoClienteAsync(
                conexion, transaccionCruda, plan.IdTenant, plan.IdCliente, pago.Importe, ct);

            if (!plan.ClienteCreditoIlimitado && nuevoSaldo > plan.ClienteLimiteCredito)
            {
                // Backstop de concurrencia (spec: Credit-Limit Evaluation) — el pre-chequeo de
                // ValidadorDePagos ya corrió AFUERA de esta transacción contra el saldo de ese
                // momento; esto atrapa una venta concurrente del MISMO cliente que subió el
                // saldo entre el pre-chequeo y este commit (test 4.10).
                throw new ErrorDominio("limite_credito_excedido", "El pago supera el límite de crédito del cliente.", 400);
            }

            await EscriturasDeCuentaCorriente.InsertarMovimientoCcAsync(
                conexion, transaccionCruda, plan.IdTenant, plan.IdCliente, plan.Momento, plan.IdPuntoVenta,
                plan.IdEmpleado, TipoMovimientoCc.Consumo, comprobante.Id, pagosEntidad[i].Id, pago.Importe, nuevoSaldo,
                detalle: null, ct);
        }

        await transaccion.CommitAsync(ct);

        return Proyectar(comprobante, itemsEntidad, pagosEntidad, plan.Items);
    }

    // ---- Resolución de datos, fuera de la transacción ----------------------------------------

    private async Task<TipoComprobante> ResolverTipoComprobanteAsync(string codigo, CancellationToken ct)
    {
        var tipo = await db.TiposComprobante.FirstOrDefaultAsync(t => t.Codigo == codigo, ct);

        // El POS solo emite tipos de venta no fiscales (TX/NCX, design: "neither of which is
        // fiscal") — un código de factura/nota real (fiscal) queda afuera de este camino a
        // propósito, no solo por no existir el flujo de facturación electrónica todavía.
        // stage-17-presupuestos-y-remitos, Slice 3 (design decisión 1, net 2 — la segunda red,
        // independiente de la desactivación del catálogo/data statement 1): incondicional sobre
        // afecta_stock, nunca condicionado a si la solicitud trae líneas de producto — un PRE
        // itemless (o cualquier otro tipo activo con afecta_stock=false, incluso fuera de banda)
        // ya no puede colarse por este resolver. RC nunca pasa por acá (tiene su propio
        // ResolverTipoRcAsync, ServicioDeCuentaCorriente.cs).
        if (tipo is null || !tipo.Activo || tipo.Clase != ClaseComprobante.Venta || tipo.EsFiscal || !tipo.AfectaStock)
        {
            throw new ErrorDominio(
                "tipo_comprobante_invalido", $"'{codigo}' no es un tipo de comprobante válido para el POS.", 400);
        }

        return tipo;
    }

    private async Task<PuntoVenta> ResolverPuntoVentaAsync(int idPuntoVenta, CancellationToken ct) =>
        await db.PuntosVenta.FirstOrDefaultAsync(pv => pv.Id == idPuntoVenta, ct)
            // El filtro de EF (+ RLS) ya deja invisible un punto de venta de otro tenant — ADR-8:
            // mismo 404 para "no existe" y "es de otro tenant".
            ?? throw ErrorDominio.NoEncontrado($"No existe el punto de venta {idPuntoVenta}.");

    private async Task<Cliente> ResolverClienteAsync(int? idCliente, CancellationToken ct)
    {
        if (idCliente is { } id)
        {
            return await db.Clientes.FirstOrDefaultAsync(c => c.Id == id, ct)
                ?? throw ErrorDominio.NoEncontrado($"No existe el cliente {id}.");
        }

        // Spec: "Omitted idCliente defaults to Consumidor Final".
        return await db.Clientes.FirstOrDefaultAsync(c => c.Numero == ReglaDeClientes.NumeroConsumidorFinal, ct)
            // Backfilleado para todo tenant desde InicializadorDeBaseDeDatos — su ausencia es un
            // bug de aprovisionamiento, no un caso de negocio alcanzable.
            ?? throw new InvalidOperationException("El tenant actual no tiene un Consumidor Final sembrado.");
    }

    /// <summary>Las tres claves que el checkout necesita, en UNA sola query <c>WHERE clave IN
    /// (...)</c> (stage-12, design decisión 2 / spec parametros-operativos: "ServicioDeVentas
    /// Batches Its Parametro Reads Into One Query") — reemplaza las dos consultas separadas de
    /// <c>tolerancia_pago</c>/<c>vuelto_maximo</c> de antes de esta etapa, agregando
    /// <c>lotes_habilitado</c> sin sumar un tercer round trip.
    ///
    /// <c>ResolucionDeParametros.Resolver</c> filtra los candidatos por punto de venta pero NO
    /// por clave (fue escrita para un candidate set de una sola clave) — pasarle el set
    /// multi-clave completo corrompería la resolución cruzada (una fila de <c>tolerancia_pago</c>
    /// con el mismo <c>id_punto_venta</c> "gana" la resolución de <c>vuelto_maximo</c>). El
    /// <c>Where(p => p.Clave == c.Clave)</c> de abajo es el target de mutación nombrado por el
    /// design (mutation-proof-tests): borrarlo tiene que tirar en rojo la prueba de
    /// <c>VentasCheckoutTests</c> que mezcla una fila de punto de venta de
    /// <c>tolerancia_pago</c> con una fila solo de empresa de <c>vuelto_maximo</c>. Evidencia de
    /// mutación registrada en ese archivo, junto al test.</summary>
    private async Task<(decimal ToleranciaPago, decimal VueltoMaximo, bool LotesHabilitado)> ResolverParametrosDeVentaAsync(
        int idEmpresa, int idPuntoVenta, CancellationToken ct)
    {
        ParametroConocido[] conocidos =
            [ParametroConocido.ToleranciaPago, ParametroConocido.VueltoMaximo, ParametroConocido.LotesHabilitado];
        var claves = conocidos.Select(c => c.Clave).ToList();

        var candidatos = await db.Parametros
            .Where(p => claves.Contains(p.Clave) && p.IdEmpresa == idEmpresa
                && (p.IdPuntoVenta == null || p.IdPuntoVenta == idPuntoVenta))
            .ToListAsync(ct);

        var resueltoPorClave = conocidos.ToDictionary(
            c => c.Clave,
            c => ResolucionDeParametros.Resolver(
                c.Clave,
                candidatos.Where(p => p.Clave == c.Clave).ToList(),
                idPuntoVenta));

        return (
            JsonSerializer.Deserialize<decimal>(resueltoPorClave[ParametroConocido.ToleranciaPago.Clave]),
            JsonSerializer.Deserialize<decimal>(resueltoPorClave[ParametroConocido.VueltoMaximo.Clave]),
            JsonSerializer.Deserialize<bool>(resueltoPorClave[ParametroConocido.LotesHabilitado.Clave]));
    }

    // ---- stage-17-presupuestos-y-remitos, Slice 3: rama del snapshot (design: Transactions —
    // "RAMA DEL SNAPSHOT") ------------------------------------------------------------------------

    /// <summary>p1-p6 del design, en orden. Corre SOLO cuando <c>SolicitudDeVenta.IdPresupuestoOrigen</c>
    /// llega presente — nunca para una venta común. Devuelve el presupuesto (para la aserción de
    /// fidelidad de totales de más arriba), sus items congelados, el "hoy" YA resuelto en la zona
    /// del punto de venta (decisión 10 — pineado UNA vez, reusado adentro de la transacción por el
    /// UPDATE guardado) y el cliente correcto (el del presupuesto, nunca lo que trajo el
    /// request).</summary>
    private async Task<(Presupuesto Presupuesto, IReadOnlyList<ItemPresupuesto> Items, DateOnly Hoy, Cliente Cliente)>
        ResolverConversionDesdePresupuestoAsync(
            int idPresupuestoOrigen, int idPuntoVenta, short signoTipoComprobante, int? idClienteSolicitado,
            DateTimeOffset momento, CancellationToken ct)
    {
        // p1: leer presupuesto (AsNoTracking) · exigir mismo tenant/PV. El filtro de tenant lo
        // aplica el query filter global de EF (+ RLS) — invisible ⇒ 404, ADR-8.
        var presupuesto = await db.Presupuestos.AsNoTracking().FirstOrDefaultAsync(p => p.Id == idPresupuestoOrigen, ct)
            ?? throw ErrorDominio.NoEncontrado($"No existe el presupuesto {idPresupuestoOrigen}.");

        if (presupuesto.IdPuntoVenta != idPuntoVenta)
        {
            throw new ErrorDominio(
                "punto_venta_no_coincide", "El presupuesto pertenece a otro punto de venta.", 400);
        }

        // p2: hoy resuelto en la zona del PV (decisión 10) — deliberadamente DISTINTO del hoy
        // UTC-naive que usa el FEFO de más abajo (tensión T4, byte-idéntico, sin cambios).
        var (_, zona) = await ResolverZonaDelPuntoVentaAsync(idPuntoVenta, ct);
        var hoy = DateOnly.FromDateTime(TimeZoneInfo.ConvertTime(momento, zona).DateTime);

        // p3: ReglaDePresupuestos.EsConvertible — PRE-CHEQUEO, NO la autoridad (design decisión
        // 5): la autoridad es el UPDATE guardado de EscriturasDePresupuesto.MarcarConvertidoAsync,
        // dentro de la transacción, en la POSICIÓN 1.5. Mismo orden de causas que
        // EscriturasDePresupuesto.ExigirCausaDelRechazoAsync usa bajo lock.
        if (presupuesto.Estado == EstadoPresupuesto.Convertido)
        {
            throw new ErrorDominio(
                "presupuesto_ya_convertido", "El presupuesto ya fue convertido en una venta.", 409);
        }

        if (presupuesto.Estado != EstadoPresupuesto.Enviado)
        {
            throw new ErrorDominio(
                "presupuesto_no_convertible", "El presupuesto no está en un estado convertible.", 409);
        }

        if (ReglaDePresupuestos.EstaVencido(presupuesto.Estado, presupuesto.Vencimiento, hoy))
        {
            throw new ErrorDominio("presupuesto_vencido", "El presupuesto está vencido.", 409);
        }

        // p4: cliente := el del presupuesto; idCliente en conflicto ⇒ 400 (nunca silenciosamente
        // pisado — un idCliente que el request trae y que no coincide es un error real, no un
        // valor a ignorar).
        if (idClienteSolicitado is { } idClienteEnConflicto && idClienteEnConflicto != presupuesto.IdCliente)
        {
            throw new ErrorDominio(
                "cliente_no_coincide", "El cliente indicado no coincide con el del presupuesto.", 400);
        }

        var cliente = await ResolverClienteAsync(presupuesto.IdCliente, ct);

        // p5: tipo.Signo <= 0 ⇒ 400 — una conversión nunca es una devolución (NCX), el
        // presupuesto congelado no tiene ninguna noción de signo negativo.
        if (signoTipoComprobante <= 0)
        {
            throw new ErrorDominio(
                "conversion_requiere_signo_positivo",
                "El tipo de comprobante de una conversión tiene que ser de signo positivo.", 400);
        }

        // p6: lineas := items del presupuesto — el llamador arma la LineaDeVenta equivalente
        // (IdLote null: FEFO se resuelve más abajo, sin cambios).
        var items = await db.ItemsPresupuesto.AsNoTracking()
            .Where(i => i.IdPresupuesto == idPresupuestoOrigen)
            .OrderBy(i => i.Orden)
            .ToListAsync(ct);

        return (presupuesto, items, hoy, cliente);
    }

    /// <summary>design decisión 10 — "hoy" del vencimiento del presupuesto se resuelve en la zona
    /// del PUNTO DE VENTA. Consulta DIRECTA (sin inyectar <c>ServicioDeParametros</c>: el binding
    /// verify criterion de esta slice acota el diff de esta clase a la rama del snapshot, y un
    /// constructor nuevo rompería los seis sitios de test que instancian <c>ServicioDeVentas</c>
    /// directo — <c>VentasCheckoutTests</c>/<c>VentasAtomicidadYConcurrenciaTests</c>/
    /// <c>PlanDeVentaFefoTests</c>/<c>VentaEscrituraLoteTests</c>/<c>VentasTurnoWiringTests</c>).
    /// Mismo criterio de resolución (<c>ResolucionDeParametros.Resolver</c>, ya importado por
    /// <see cref="ResolverParametrosDeVentaAsync"/>) que <c>ServicioDePresupuestos.ResolverZonaAsync</c>.</summary>
    private async Task<(string ZonaId, TimeZoneInfo Zona)> ResolverZonaDelPuntoVentaAsync(int idPuntoVenta, CancellationToken ct)
    {
        var puntoVenta = await db.PuntosVenta.AsNoTracking()
            .Where(pv => pv.Id == idPuntoVenta)
            .Select(pv => new { pv.IdEmpresa })
            .FirstOrDefaultAsync(ct)
            ?? throw ErrorDominio.NoEncontrado($"No existe el punto de venta {idPuntoVenta}.");

        var candidatos = await db.Parametros
            .Where(p => p.Clave == ParametroConocido.ZonaHoraria.Clave && p.IdEmpresa == puntoVenta.IdEmpresa
                && (p.IdPuntoVenta == null || p.IdPuntoVenta == idPuntoVenta))
            .ToListAsync(ct);

        var zonaId = JsonSerializer.Deserialize<string>(
            ResolucionDeParametros.Resolver(ParametroConocido.ZonaHoraria.Clave, candidatos, idPuntoVenta))!;

        return (zonaId, TimeZoneInfo.FindSystemTimeZoneById(zonaId));
    }

    /// <summary>design decisión 3, fact 2: el SEGUNDO materializador privado — <see
    /// cref="MaterializarItems"/> (más abajo) queda literalmente intacto. Ambos corren
    /// <see cref="CalculadorDeTotales.Calcular"/> como única autoridad aritmética (tensión T2).
    /// <c>precio_unitario</c>/<c>descuento</c>/<c>id_lista_precio</c>/<c>id_oferta</c>/
    /// <c>id_alicuota_iva</c>/<c>porcentaje_iva</c>/<c>descripcion</c> SIEMPRE salen congeladas de
    /// <paramref name="itemsPresupuesto"/> (mutation targets 25-28), nunca re-resueltas contra el
    /// catálogo de hoy — solo <c>costo_unitario</c> sale de <c>articulo.CostoNominal</c> de HOY
    /// (mutation target 29, decisión 4 del proposal: el costo es a la fecha de emisión, no de
    /// cotización).</summary>
    private static (IReadOnlyList<LineaDelPlan> Items, TotalesCalculados Totales) MaterializarItemsDesdePresupuesto(
        IReadOnlyList<ItemPresupuesto> itemsPresupuesto,
        IReadOnlyDictionary<int, Articulo> articuloPorId)
    {
        // OD9/T3: un único id_lista_precio para todo el documento es un invariante DE SERVICIO,
        // nunca una restricción de esquema — items_presupuesto lo carga por línea. Un presupuesto
        // se cotiza siempre contra UNA resolución, así que un desacuerdo acá es un invariante de
        // escritura roto, no un caso de negocio.
        var idsListaPrecio = itemsPresupuesto.Select(i => i.IdListaPrecio).Distinct().ToList();
        if (idsListaPrecio.Count > 1)
        {
            throw new InvalidOperationException(
                $"El presupuesto tiene items con distintas listas de precio ({string.Join(", ", idsListaPrecio)}) " +
                "— invariante de servicio violado (una lista por documento).");
        }

        var lineasParaCalcular = itemsPresupuesto
            .Select(i => new LineaParaCalcular(
                i.Cantidad, i.PrecioUnitario, i.Cantidad == 0m ? 0m : i.Descuento / i.Cantidad))
            .ToList();

        var totales = CalculadorDeTotales.Calcular(lineasParaCalcular);

        // Defensa en profundidad (design: Domain rules first) — un presupuesto nunca es una
        // devolución (p5 arriba ya exige signo positivo), así que esto siempre pasa; si no pasa,
        // es un bug de esta clase.
        ReglaDeComprobantes.ValidarSignoDeLineas(signoTipoComprobante: (short)1, totales.Items.Select(it => it.Cantidad).ToList());

        var items = new List<LineaDelPlan>(itemsPresupuesto.Count);
        for (var i = 0; i < itemsPresupuesto.Count; i++)
        {
            var item = itemsPresupuesto[i];
            var calculado = totales.Items[i];
            var articulo = articuloPorId[item.IdArticulo];

            items.Add(new LineaDelPlan(
                item.IdArticulo, item.Descripcion, CodigoBarra: null, articulo.IdArea,
                item.IdListaPrecio, item.IdOferta, item.IdAlicuotaIva, item.PorcentajeIva,
                calculado.Cantidad, calculado.PrecioUnitario, calculado.Descuento, calculado.Total,
                articulo.EsProducto, articulo.CostoNominal));
        }

        return (items, totales);
    }

    /// <summary>Corre <see cref="CalculadorDeTotales"/> (pura) sobre las líneas ya resueltas y
    /// arma el resto del snapshot de cada item — design decisión 3: <see cref="LineaDeVenta"/>
    /// siempre manda <see cref="LineaDeVenta.Cantidad"/> POSITIVA (el operador piensa en
    /// unidades, no en signo contable); el signo lo aplica esta clase a partir de
    /// <c>tipos_comprobante.signo</c> ANTES de calcular — así <c>CalculadorDeTotales</c> hace la
    /// misma aritmética para TX y NCX, sin rama especial (design: decisión 4, "aritmética
    /// uniforme para ambos signos").</summary>
    private static (IReadOnlyList<LineaDelPlan> Items, TotalesCalculados Totales) MaterializarItems(
        short signoTipoComprobante,
        IReadOnlyList<LineaDeVenta> lineas,
        IReadOnlyList<ResultadoDeResolucion> resolucion,
        IReadOnlyDictionary<int, Articulo> articuloPorId,
        IReadOnlyDictionary<int, decimal> porcentajePorAlicuota,
        int idListaPrecio)
    {
        var lineasParaCalcular = new List<LineaParaCalcular>(lineas.Count);

        for (var i = 0; i < lineas.Count; i++)
        {
            var resultado = resolucion[i];

            if (resultado.PrecioOriginal is null)
            {
                throw new ErrorDominio(
                    "articulo_sin_precio_vigente",
                    $"El artículo {lineas[i].IdArticulo} no tiene un precio vigente en la lista del cliente.",
                    400);
            }

            var cantidadConSigno = signoTipoComprobante > 0 ? lineas[i].Cantidad : -lineas[i].Cantidad;

            lineasParaCalcular.Add(new LineaParaCalcular(
                cantidadConSigno, resultado.PrecioOriginal.Value, resultado.DescuentoUnitario));
        }

        var totales = CalculadorDeTotales.Calcular(lineasParaCalcular);

        // Defensa en profundidad (design: Domain rules first) — con el signo ya aplicado arriba,
        // esta llamada siempre debería pasar; si no pasa, es un bug de esta clase, no un caso de
        // negocio alcanzable.
        ReglaDeComprobantes.ValidarSignoDeLineas(signoTipoComprobante, totales.Items.Select(it => it.Cantidad).ToList());

        var items = new List<LineaDelPlan>(lineas.Count);

        for (var i = 0; i < lineas.Count; i++)
        {
            var linea = lineas[i];
            var resultado = resolucion[i];
            var calculado = totales.Items[i];
            var articulo = articuloPorId[linea.IdArticulo];

            // Solo UNA columna id_oferta por item (esquema): cuando se acumulan varias ofertas
            // en la misma línea, se snapshotea la de mayor prioridad (la primera de la lista que
            // arma ResolvedorDeOfertas) — el descuento total sigue siendo la suma de todas, ese
            // no se pierde.
            var idOferta = resultado.Aplicadas.Count > 0 ? resultado.Aplicadas[0].IdOferta : (int?)null;

            items.Add(new LineaDelPlan(
                articulo.Id, articulo.Nombre, linea.CodigoBarra, articulo.IdArea, idListaPrecio, idOferta,
                articulo.IdAlicuotaIva, porcentajePorAlicuota[articulo.IdAlicuotaIva],
                calculado.Cantidad, calculado.PrecioUnitario, calculado.Descuento, calculado.Total,
                articulo.EsProducto, articulo.CostoNominal));
        }

        return (items, totales);
    }

    // ---- Statements crudos de la transacción (ADO.NET, misma convención que
    // AsignadorDeNumeroComprobante) --------------------------------------------------------------

    /// <summary>Design: The Sale Transaction, paso 5 — INSERT simple, sin upsert: usa
    /// <c>ExecuteNonQueryAsync</c> (nunca dispara <c>ReaderExecuting</c>/<c>ScalarExecuting</c>),
    /// así que el guard de presupuesto de consultas (task 4.12) no lo ve. Ojo con la simplificación
    /// fácil acá: el guard SÍ ve los dos <c>SaveChangesAsync</c> de la mitad transaccional
    /// (comprobante e items/pagos, <see cref="EjecutarTransaccionAsync"/>) — Npgsql dispara
    /// <c>ReaderExecuting</c> también en un <c>INSERT ... RETURNING</c> para leer la clave
    /// generada, no solo en un <c>SELECT</c>. El presupuesto sigue dando un número constante
    /// porque esos dos <c>SaveChangesAsync</c> no escalan con la cantidad de líneas/pagos; esta
    /// escritura sí escala (una por línea) y por quedar fuera de <c>ReaderExecuting</c> no aporta
    /// al conteo — no es un N+1 a corregir, es auténticamente invisible al guard.</summary>
    private static async Task InsertarMovimientoStockAsync(
        DbConnection conexion, DbTransaction? transaccion, int idTenant, int idArticulo, int idPuntoVenta,
        decimal cantidad, MotivoStock motivo, int idComprobanteVenta, int idEmpleado, DateTimeOffset creadoEl,
        int? idLote, CancellationToken ct)
    {
        await using var comando = conexion.CreateCommand();
        comando.Transaction = transaccion;
        comando.CommandText =
            "INSERT INTO movimientos_stock " +
            "(id_tenant, id_articulo, id_punto_venta, cantidad, motivo, id_comprobante_venta, id_empleado, creado_el, id_lote) " +
            "VALUES ($1, $2, $3, $4, $5, $6, $7, $8, $9)";

        ParametrosDeComando.Agregar(comando, idTenant);
        ParametrosDeComando.Agregar(comando, idArticulo);
        ParametrosDeComando.Agregar(comando, idPuntoVenta);
        ParametrosDeComando.Agregar(comando, cantidad);
        ParametrosDeComando.Agregar(comando, motivo);
        ParametrosDeComando.Agregar(comando, idComprobanteVenta);
        ParametrosDeComando.Agregar(comando, idEmpleado);
        ParametrosDeComando.Agregar(comando, creadoEl);
        ParametrosDeComando.AgregarNulo(comando, idLote);

        await comando.ExecuteNonQueryAsync(ct);
    }

    /// <summary>Design decisión 1: el único statement que toca <c>stock</c> — su propio row lock
    /// (implícito en el <c>INSERT ... ON CONFLICT</c>) es lo que reemplaza al advisory lock.
    /// <c>RETURNING</c> vía <c>ExecuteScalarAsync</c> (no <c>ExecuteReaderAsync</c>): mismo
    /// motivo que <see cref="InsertarMovimientoStockAsync"/>, invisible al guard de presupuesto.</summary>
    private static async Task UpsertStockAsync(
        DbConnection conexion, DbTransaction? transaccion, int idTenant, int idArticulo, int idPuntoVenta,
        decimal delta, CancellationToken ct)
    {
        await using var comando = conexion.CreateCommand();
        comando.Transaction = transaccion;
        comando.CommandText =
            "INSERT INTO stock (id_articulo, id_punto_venta, id_tenant, cantidad) " +
            "VALUES ($1, $2, $3, $4) " +
            "ON CONFLICT (id_articulo, id_punto_venta) DO UPDATE " +
            "SET cantidad = stock.cantidad + EXCLUDED.cantidad " +
            "RETURNING cantidad";

        ParametrosDeComando.Agregar(comando, idArticulo);
        ParametrosDeComando.Agregar(comando, idPuntoVenta);
        ParametrosDeComando.Agregar(comando, idTenant);
        ParametrosDeComando.Agregar(comando, delta);

        await comando.ExecuteScalarAsync(ct);
    }

    /// <summary>Etapa 12, slice 8 (design: "Write site 1" — "UpsertStockLoteAsync: la MISMA forma
    /// que UpsertStockAsync, una clave más"): mismo shape que
    /// <see cref="Ways.Application.Compras.ServicioDeCompras"/>'s sibling (Slice 5) y
    /// <c>ServicioDeLotes.ResolverOCrearAsync</c> (Slice 3) — <c>INSERT ... ON CONFLICT DO UPDATE
    /// ... RETURNING</c>, nunca <c>DO NOTHING</c>. Sin chequeo de negativo acá (a diferencia de
    /// compras): revertir una venta siempre SUMA de vuelta, nunca puede dejar el saldo negativo.</summary>
    private static async Task UpsertStockLoteAsync(
        DbConnection conexion, DbTransaction? transaccion, int idTenant, int idArticulo, int idPuntoVenta,
        int idLote, decimal delta, CancellationToken ct)
    {
        await using var comando = conexion.CreateCommand();
        comando.Transaction = transaccion;
        comando.CommandText =
            "INSERT INTO stock_lotes (id_articulo, id_punto_venta, id_lote, id_tenant, cantidad) " +
            "VALUES ($1, $2, $3, $4, $5) " +
            "ON CONFLICT (id_articulo, id_punto_venta, id_lote) DO UPDATE " +
            "SET cantidad = stock_lotes.cantidad + EXCLUDED.cantidad " +
            "RETURNING cantidad";

        ParametrosDeComando.Agregar(comando, idArticulo);
        ParametrosDeComando.Agregar(comando, idPuntoVenta);
        ParametrosDeComando.Agregar(comando, idLote);
        ParametrosDeComando.Agregar(comando, idTenant);
        ParametrosDeComando.Agregar(comando, delta);

        await comando.ExecuteScalarAsync(ct);
    }

    private async Task<DbConnection> ObtenerConexionAbiertaAsync(CancellationToken ct)
    {
        var conexion = db.Database.GetDbConnection();

        if (conexion.State != ConnectionState.Open)
        {
            await db.Database.OpenConnectionAsync(ct);
        }

        return conexion;
    }

    // ---- Utilidades ---------------------------------------------------------------------------

    /// <summary>Snapshot informativo, nunca clave de negocio (ver doc-comment de
    /// <see cref="LineaDeVenta.CodigoBarra"/>) — el tope solo evita que un payload arbitrariamente
    /// largo llegue a <c>items_comprobante_venta.codigo_barra</c>.</summary>
    private const int LongitudMaximaCodigoBarra = 64;

    private static IReadOnlyList<LineaDeVenta> ExigirLineasValidas(IReadOnlyList<LineaDeVenta>? lineas)
    {
        if (lineas is null || lineas.Count == 0)
        {
            throw new ErrorDominio("lineas_requeridas", "El carrito tiene que tener al menos una línea.", 400);
        }

        foreach (var linea in lineas)
        {
            if (linea.Cantidad <= 0)
            {
                throw new ErrorDominio(
                    "cantidad_de_linea_invalida", "La cantidad de cada línea tiene que ser mayor a cero.", 400);
            }

            // Máximo 3 decimales (doc 10: cantidad soporta fracción para UnidadVenta.Peso, pero
            // sin precisión ilimitada) — decimal.Round con MidpointRounding.AwayFromZero nunca
            // altera un valor que ya tiene ≤ 3 decimales, así que la comparación detecta
            // exactamente el exceso de precisión sin falsos positivos por redondeo bancario.
            if (decimal.Round(linea.Cantidad, 3, MidpointRounding.AwayFromZero) != linea.Cantidad)
            {
                throw new ErrorDominio(
                    "cantidad_invalida", "La cantidad de cada línea admite hasta 3 decimales.", 400);
            }

            if (linea.CodigoBarra is { Length: > LongitudMaximaCodigoBarra })
            {
                throw new ErrorDominio(
                    "codigo_barra_invalido", $"El código de barra no puede superar los {LongitudMaximaCodigoBarra} caracteres.", 400);
            }
        }

        return lineas;
    }

    /// <summary>stage-17-presupuestos-y-remitos, Slice 3 (design: Transactions — ":59", mutation
    /// target 36): la mitad complementaria de <see cref="ExigirLineasValidas"/> — con
    /// <c>idPresupuestoOrigen</c> presente, <c>lineas</c> tiene que llegar vacío/ausente
    /// (<c>dto-contract-honesty</c> regla 1: un campo que el servidor ignoraría en silencio no se
    /// acepta). Devuelve una lista vacía — el valor REAL se resuelve después, en la rama del
    /// snapshot (p6), reasignando la variable del llamador.</summary>
    private static IReadOnlyList<LineaDeVenta> ExigirSinLineas(IReadOnlyList<LineaDeVenta>? lineas)
    {
        if (lineas is { Count: > 0 })
        {
            throw new ErrorDominio(
                "lineas_no_admitidas",
                "Una venta con idPresupuestoOrigen no admite líneas propias: el precio sale del presupuesto.",
                400);
        }

        return [];
    }

    private static string? NormalizarOpcional(string? valor)
    {
        var limpio = valor?.Trim();
        return string.IsNullOrEmpty(limpio) ? null : limpio;
    }

    private async Task<ComprobanteVenta> BuscarComprobanteAsync(int id, CancellationToken ct) =>
        await db.ComprobantesVenta.FirstOrDefaultAsync(c => c.Id == id, ct)
            ?? throw ErrorDominio.NoEncontrado($"No existe el comprobante {id}.");

    private int ExigirTenantDeLaSesion() =>
        contexto.IdTenant
            // OperacionDePos (capa de API) ya exige un actor de tenant (Vendedor/Supervisor/
            // Admin) — un actor de plataforma nunca llega hasta acá. Defensa en profundidad, no
            // un camino alcanzable en operación normal.
            ?? throw new InvalidOperationException(
                "ServicioDeVentas requiere un actor de tenant; OperacionDePos no admite plataforma.");

    /// <summary>Design decisión (stage-12 slice 7, actualizada en slice 8): para el checkout recién
    /// emitido, <paramref name="planItems"/> trae el lote ya resuelto en la fase de decisión (mismo
    /// orden/índice que <paramref name="items"/>, uno a uno vía <c>Orden</c>) y lo proyecta acá; para
    /// una relectura sin plan a mano (reprint, idempotencia de
    /// <see cref="BuscarPorNumeroComprometidoAsync"/>) cae al valor ya persistido en la entidad —
    /// desde slice 8 ambos caminos devuelven el MISMO <c>id_lote</c> (el snapshot de
    /// <c>items_comprobante_venta.id_lote</c> ya se escribe en <see cref="EjecutarTransaccionAsync"/>),
    /// el fallback queda solo por si algún día <paramref name="planItems"/> falta.</summary>
    private static ComprobanteEmitido Proyectar(
        ComprobanteVenta comprobante, IReadOnlyList<ItemComprobanteVenta> items, IReadOnlyList<PagoComprobante> pagos,
        IReadOnlyList<LineaDelPlan>? planItems = null) => new(
        comprobante.Id, comprobante.Numero,
        NumeroDeComprobante.Formatear(comprobante.IdPuntoVenta, comprobante.Numero),
        comprobante.Estado, comprobante.Fecha, comprobante.IdPuntoVenta, comprobante.IdCliente,
        comprobante.IdComprobanteAsociado, comprobante.Subtotal, comprobante.DescuentoTotal, comprobante.Total,
        comprobante.DireccionEntrega, comprobante.Observaciones,
        items
            .OrderBy(i => i.Orden)
            .Select(i =>
            {
                var planItem = planItems?[i.Orden - 1];
                return new ItemEmitido(
                    i.Orden, i.IdArticulo, i.Descripcion, i.CodigoBarra, i.IdArea, i.IdListaPrecio, i.IdOferta,
                    i.IdAlicuotaIva, i.PorcentajeIva, i.Cantidad, i.PrecioUnitario, i.Descuento, i.Total,
                    planItem?.IdLote ?? i.IdLote, planItem?.CodigoLote, planItem?.LoteVencido ?? false);
            })
            .ToList(),
        pagos
            .Select(p => new PagoEmitido(p.IdMedioPago, p.Importe, p.Referencia, p.Vuelto))
            .ToList(),
        comprobante.IdPresupuestoOrigen);

    // ---- El plan inmutable (design: "PlanDeVenta(immutable)") --------------------------------

    private readonly record struct LineaDelPlan(
        int IdArticulo, string Descripcion, string? CodigoBarra, int IdArea, int IdListaPrecio, int? IdOferta,
        int IdAlicuotaIva, decimal PorcentajeIva, decimal Cantidad, decimal PrecioUnitario, decimal Descuento,
        decimal Total, bool EsProducto, decimal? CostoUnitario,
        int? IdLote = null, string? CodigoLote = null, bool LoteVencido = false);

    private readonly record struct PagoDelPlan(
        int IdMedioPago, ComportamientoMedioPago Comportamiento, decimal Importe, string? Referencia, decimal Vuelto);

    private sealed record PlanDeVenta(
        int IdTenant,
        int IdEmpleado,
        int IdTipoComprobante,
        string CodigoTipoComprobante,
        DateTimeOffset Momento,
        int IdPuntoVenta,
        int IdTurnoCaja,
        int IdCliente,
        int? IdComprobanteAsociado,
        IReadOnlyList<LineaDelPlan> Items,
        decimal Subtotal,
        decimal DescuentoTotal,
        decimal Total,
        IReadOnlyList<PagoDelPlan> Pagos,
        decimal ClienteLimiteCredito,
        bool ClienteCreditoIlimitado,
        string? DireccionEntrega,
        string? Observaciones,
        // stage-17-presupuestos-y-remitos, Slice 3: IdPresupuestoOrigen null ⇒ venta común, CERO
        // statements extra en EjecutarTransaccionAsync/EjecutarAnulacionAsync (design decisión 6).
        // HoyDelPuntoVenta viaja YA resuelto (design decisión 10) — pineado una vez en la fase
        // decide, nunca vuelto a calcular dentro de la transacción reintentable.
        int? IdPresupuestoOrigen = null,
        DateOnly? HoyDelPuntoVenta = null);
}
