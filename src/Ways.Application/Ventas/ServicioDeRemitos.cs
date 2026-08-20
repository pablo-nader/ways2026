using System.Data;
using System.Data.Common;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Storage;
using Ways.Application.Abstracciones;
using Ways.Application.Ofertas;
using Ways.Application.Parametros;
using Ways.Application.Stock;
using Ways.Domain.Articulos;
using Ways.Domain.Catalogos;
using Ways.Domain.Clientes;
using Ways.Domain.Common;
using Ways.Domain.Organizacion;
using Ways.Domain.Stock;
using Ways.Domain.Ventas;

namespace Ways.Application.Ventas;

/// <summary>
/// stage-17-presupuestos-y-remitos, Slice 5 (design: Technical Approach fact 6/decisión 8; API
/// Surface). Borrador CRUD (replace-set completo bajo <c>SELECT … FOR UPDATE … WHERE
/// estado='borrador'</c>, mismo criterio que <see cref="ServicioDePresupuestos"/>/
/// <c>ServicioDeOrdenesDeCompra</c>) + <see cref="EmitirAsync"/> — EL CUARTO WRITE SITE DE STOCK,
/// IMPLEMENTADO INDEPENDIENTE (design decisión 8/tasks 5.3-5.6): numeración propia (serie
/// <c>'REM'</c>), FEFO resuelto ANTES de la transacción con el mismo <c>hoy</c> UTC-naive del
/// checkout (decisión 10, paridad deliberada — mutation target 47), y su propio orden de lock
/// ascendente <c>(id_articulo, id_lote NULLS FIRST)</c> — sus propios statements crudos de
/// stock/stock_lotes/movimientos_stock, SIN compartir helper con <c>ServicioDeVentas</c> (la
/// duplicación es el método de prueba del contrato de <c>stock/spec.md:178-189</c>, jamás
/// refactorizada). + <see cref="AnularAsync"/> (borrador/emitido → anulado, facturado → 409, sin
/// chequeo de negativo — decisión 9: un remito decrementa, su reversa siempre suma).
///
/// A diferencia de <see cref="ServicioDePresupuestos"/>: sin vencimiento, sin zona horaria por
/// punto de venta, sin turno (decisión 13 del proposal: un remito mueve mercadería, no dinero — la
/// consolidación de Slice 6 sí lo exige).
/// </summary>
public class ServicioDeRemitos(
    IWaysDbContext db, IRelojDelSistema reloj, IContextoDeUsuario contexto, ServicioDeOfertas servicioDeOfertas,
    ServicioDeLotes servicioDeLotes)
{
    // ---- lectura: listado paginado + detalle (task 5.10, mirrors 2.8) ------------------------------

    public async Task<PaginaDeRemitos> ListarAsync(
        int? idPuntoVenta = null,
        int? idCliente = null,
        EstadoRemito? estado = null,
        DateTimeOffset? desde = null,
        DateTimeOffset? hasta = null,
        int pagina = 1,
        int tamanio = 25,
        CancellationToken ct = default)
    {
        pagina = Math.Max(pagina, 1);
        tamanio = Math.Clamp(tamanio, 1, 200);

        var query = ConstruirQuery(idPuntoVenta, idCliente, estado, desde, hasta);

        var total = await query.CountAsync(ct);

        var pagados = await query
            .OrderByDescending(r => r.FechaEmision)
            .ThenByDescending(r => r.Id)
            .Skip((pagina - 1) * tamanio)
            .Take(tamanio)
            .ToListAsync(ct);

        var items = pagados.Select(ProyectarListado).ToList();

        return new PaginaDeRemitos(items, total, pagina, tamanio);
    }

    /// <summary>Cláusulas bajo prueba (<c>mutation-proof-tests</c>, mutation target #59, mitad
    /// remito), mismo criterio que <c>ServicioDePresupuestos.ConstruirQuery</c>: <c>Where(r =>
    /// r.IdPuntoVenta == pv)</c>/<c>Where(r => r.IdCliente == c)</c> → un remito filtra los de
    /// otro; <c>ThenByDescending(r => r.Id)</c> → con <c>fecha_emision</c> empatada (<c>RelojFijo</c>)
    /// la paginación duplica; cada <c>if (idPuntoVenta/idCliente/estado/desde/hasta is { } x)</c> →
    /// filtro ignorado.</summary>
    private IQueryable<Remito> ConstruirQuery(
        int? idPuntoVenta, int? idCliente, EstadoRemito? estado, DateTimeOffset? desde, DateTimeOffset? hasta)
    {
        var query = db.Remitos.AsQueryable();

        if (idPuntoVenta is { } pv)
        {
            query = query.Where(r => r.IdPuntoVenta == pv);
        }

        if (idCliente is { } c)
        {
            query = query.Where(r => r.IdCliente == c);
        }

        if (estado is { } e)
        {
            query = query.Where(r => r.Estado == e);
        }

        if (desde is { } d)
        {
            query = query.Where(r => r.FechaEmision >= d);
        }

        if (hasta is { } h)
        {
            query = query.Where(r => r.FechaEmision <= h);
        }

        return query;
    }

    public async Task<RemitoDetalle> ObtenerDetalleAsync(int id, CancellationToken ct = default)
    {
        var remito = await db.Remitos.AsNoTracking().FirstOrDefaultAsync(r => r.Id == id, ct)
            ?? throw ErrorDominio.NoEncontrado($"No existe el remito {id}.");

        var items = await db.ItemsRemito.AsNoTracking()
            .Where(i => i.IdRemito == id)
            .OrderBy(i => i.Orden)
            .ToListAsync(ct);

        return ProyectarDetalle(remito, items);
    }

    // ---- borrador: crear + replace-set (mismo criterio que ServicioDePresupuestos) -----------------

    /// <summary>design: Technical Approach (fact 1), task 5.2: los precios se resuelven al
    /// GUARDAR el borrador — la misma <see cref="ServicioDeOfertas"/> que usa el checkout/
    /// presupuesto, nunca una segunda autoridad. <see cref="LineaDeRemito.IdLote"/>, si viene, se
    /// pre-chequea contra <c>lotes</c> (backstop map FK 22) y persiste directo en
    /// <c>items_remito.id_lote</c> — el pick explícito que <see cref="EmitirAsync"/> honra (ver el
    /// doc-comment de <see cref="LineaDeRemito"/>).</summary>
    public async Task<RemitoDetalle> CrearBorradorAsync(SolicitudDeRemito solicitud, CancellationToken ct = default)
    {
        var idTenant = ExigirTenantDeLaSesion();
        var idEmpleado = contexto.UsuarioId;
        var momento = reloj.Ahora;

        var puntoVenta = await ResolverPuntoVentaAsync(solicitud.IdPuntoVenta, ct);
        var cliente = await ResolverClienteAsync(solicitud.IdCliente, ct);
        ExigirCantidadesValidas(solicitud.Lineas);

        var (lineasMaterializadas, totales) = await ResolverYMaterializarAsync(
            solicitud.Lineas, idTenant, puntoVenta.IdEmpresa, cliente.IdListaPrecio, momento, ct);

        var remito = new Remito
        {
            IdTenant = idTenant,
            IdPuntoVenta = solicitud.IdPuntoVenta,
            IdCliente = cliente.Id,
            IdEmpleado = idEmpleado,
            Numero = null,
            FechaEmision = momento,
            FechaSalida = null,
            DireccionEntrega = NormalizarOpcional(solicitud.DireccionEntrega),
            Observaciones = NormalizarOpcional(solicitud.Observaciones),
            Subtotal = totales.Subtotal,
            DescuentoTotal = totales.DescuentoTotal,
            Total = totales.Total,
            Estado = EstadoRemito.Borrador,
            CreatedAt = momento,
            UpdatedAt = momento
        };
        db.Remitos.Add(remito);
        await db.SaveChangesAsync(ct);

        var items = ConstruirItems(remito.Id, idTenant, momento, lineasMaterializadas);
        db.ItemsRemito.AddRange(items);
        await db.SaveChangesAsync(ct);

        return ProyectarDetalle(remito, items);
    }

    /// <summary>Mismo criterio que <c>ServicioDePresupuestos.EditarAsync</c>: replace-set completo
    /// bajo <c>SELECT … FOR UPDATE … WHERE estado='borrador'</c> — el predicado de estado en el
    /// mismo statement hace que editar un remito ya emitido sea estructuralmente imposible. El
    /// <c>RemoveRange</c> está scopeado por <c>IdRemito</c> — un remito hermano del mismo tenant,
    /// con sus propios items, queda intacto (rule 12c).</summary>
    public async Task<RemitoDetalle> EditarAsync(int id, SolicitudDeRemito solicitud, CancellationToken ct = default)
    {
        var idTenant = ExigirTenantDeLaSesion();
        var momento = reloj.Ahora;

        var puntoVenta = await ResolverPuntoVentaAsync(solicitud.IdPuntoVenta, ct);
        var cliente = await ResolverClienteAsync(solicitud.IdCliente, ct);
        ExigirCantidadesValidas(solicitud.Lineas);

        var (lineasMaterializadas, totales) = await ResolverYMaterializarAsync(
            solicitud.Lineas, idTenant, puntoVenta.IdEmpresa, cliente.IdListaPrecio, momento, ct);

        var estrategia = FabricaDeEstrategiaSinReintento.CrearEstrategiaSinReintento(db);
        return await estrategia.ExecuteAsync(async () =>
            await EjecutarEdicionAsync(id, idTenant, solicitud, cliente.Id, lineasMaterializadas, totales, momento, ct));
    }

    private async Task<RemitoDetalle> EjecutarEdicionAsync(
        int id, int idTenant, SolicitudDeRemito solicitud, int idCliente,
        IReadOnlyList<LineaMaterializada> lineasMaterializadas, TotalesCalculados totales, DateTimeOffset momento,
        CancellationToken ct)
    {
        await using var transaccion = await db.Database.BeginTransactionAsync(ct);

        var conexion = await ObtenerConexionAbiertaAsync(ct);
        var transaccionCruda = db.Database.CurrentTransaction?.GetDbTransaction();

        var bloqueado = await BloquearBorradorAsync(conexion, transaccionCruda, id, idTenant, ct);
        if (!bloqueado)
        {
            var existe = await db.Remitos.AsNoTracking().AnyAsync(r => r.Id == id, ct);
            if (!existe)
            {
                throw ErrorDominio.NoEncontrado($"No existe el remito {id}.");
            }

            throw new ErrorDominio("remito_no_editable", "Solo un remito en borrador puede editarse.", 409);
        }

        var remito = await db.Remitos.FirstAsync(r => r.Id == id, ct);

        var itemsExistentes = await db.ItemsRemito.Where(i => i.IdRemito == id).ToListAsync(ct);
        db.ItemsRemito.RemoveRange(itemsExistentes);

        remito.IdPuntoVenta = solicitud.IdPuntoVenta;
        remito.IdCliente = idCliente;
        remito.DireccionEntrega = NormalizarOpcional(solicitud.DireccionEntrega);
        remito.Observaciones = NormalizarOpcional(solicitud.Observaciones);
        remito.Subtotal = totales.Subtotal;
        remito.DescuentoTotal = totales.DescuentoTotal;
        remito.Total = totales.Total;
        remito.UpdatedAt = momento;

        var itemsNuevos = ConstruirItems(id, idTenant, momento, lineasMaterializadas);
        db.ItemsRemito.AddRange(itemsNuevos);

        await db.SaveChangesAsync(ct);
        await transaccion.CommitAsync(ct);

        return ProyectarDetalle(remito, itemsNuevos);
    }

    // ---- emitir: numeración propia + el CUARTO write site de stock (design decisión 8) -------------

    /// <summary>design: Transactions — "EMITIR REMITO", task 5.3-5.7. FEFO se resuelve ANTES de
    /// abrir la transacción, con <c>hoy</c> UTC-naive (paridad deliberada con el checkout — decisión
    /// 10, mutation target 47). Refusa <c>remito_sin_items</c> (400) y una línea con
    /// <c>EsProducto = false</c> (<c>articulo_no_es_producto</c>, 400) antes de reservar el número —
    /// mismo criterio que <c>ServicioDePresupuestos.EnviarAsync</c> con
    /// <c>presupuesto_sin_items</c>.</summary>
    public async Task<RemitoDetalle> EmitirAsync(int id, CancellationToken ct = default)
    {
        var idTenant = ExigirTenantDeLaSesion();
        var idEmpleado = contexto.UsuarioId;
        var momento = reloj.Ahora;

        var preLectura = await db.Remitos.AsNoTracking().FirstOrDefaultAsync(r => r.Id == id, ct)
            ?? throw ErrorDominio.NoEncontrado($"No existe el remito {id}.");

        if (preLectura.Estado != EstadoRemito.Borrador)
        {
            throw new ErrorDominio("remito_ya_emitido", "El remito ya no está en borrador.", 409);
        }

        var items = await db.ItemsRemito.AsNoTracking()
            .Where(i => i.IdRemito == id)
            .OrderBy(i => i.Orden)
            .ToListAsync(ct);

        // Mutation target 43 (mitad remito_sin_items): un remito vacío nunca gasta un número.
        if (items.Count == 0)
        {
            throw new ErrorDominio("remito_sin_items", "El remito no tiene items para emitir.", 400);
        }

        var idsArticulo = items.Select(i => i.IdArticulo).Distinct().ToList();
        var articuloPorId = await db.Articulos
            .Where(a => idsArticulo.Contains(a.Id))
            .ToDictionaryAsync(a => a.Id, ct);

        // Mutation target 43 (mitad articulo_no_es_producto): remueve por completo la rama
        // "skip de servicio" del checkout (:867) para el cuarto write site — acá TODA línea tiene
        // que mover stock, o se rechaza antes de escribir nada.
        var lineaNoProducto = items.FirstOrDefault(i => !articuloPorId[i.IdArticulo].EsProducto);
        if (lineaNoProducto is not null)
        {
            throw new ErrorDominio(
                "articulo_no_es_producto",
                $"El artículo {lineaNoProducto.IdArticulo} no es un producto; no puede salir por remito.",
                400);
        }

        var idPuntoVenta = preLectura.IdPuntoVenta;
        var puntoVenta = await db.PuntosVenta.AsNoTracking().FirstAsync(pv => pv.Id == idPuntoVenta, ct);

        var loteFinalPorItem = await ResolverFefoAsync(
            idTenant, idPuntoVenta, puntoVenta.IdEmpresa, items, articuloPorId, momento, ct);

        var estrategiaNumeracion = db.Database.CreateExecutionStrategy();
        var numero = await estrategiaNumeracion.ExecuteAsync(async () =>
            await AsignadorDeNumeroComprobante.AsignarComprometidoAsync(db, idTenant, idPuntoVenta, "REM", ct));

        var estrategia = FabricaDeEstrategiaSinReintento.CrearEstrategiaSinReintento(db);
        return await estrategia.ExecuteAsync(async () =>
            await EjecutarEmisionAsync(
                id, idTenant, idPuntoVenta, idEmpleado, numero, momento, items, articuloPorId, loteFinalPorItem, ct));
    }

    /// <summary>design decisión 10/task 5.4: FEFO fuera de la transacción, <c>hoy</c> UTC-naive —
    /// BYTE-IDÉNTICO al árbol de decisión de <c>ServicioDeVentas.EmitirAsync</c> (:240-297), salvo
    /// que el pick EXPLÍCITO ya viene persistido en <c>ItemRemito.IdLote</c> desde el borrador (ver
    /// el doc-comment de <see cref="LineaDeRemito"/>) en vez de llegar en la solicitud de esta
    /// llamada — <c>emitir</c> no toma body. Devuelve el lote final por <c>id_item</c>; una línea
    /// sin lote efectivo no aparece en el diccionario (queda <c>NULL</c> en el movimiento).</summary>
    private async Task<IReadOnlyDictionary<int, SaldoDeLote>> ResolverFefoAsync(
        int idTenant, int idPuntoVenta, int idEmpresa, IReadOnlyList<ItemRemito> items,
        IReadOnlyDictionary<int, Articulo> articuloPorId, DateTimeOffset momento, CancellationToken ct)
    {
        var resultado = new Dictionary<int, SaldoDeLote>();

        var lotesHabilitado = await ResolverLotesHabilitadoAsync(idEmpresa, idPuntoVenta, ct);

        var lineasConLote = items
            .Where(i => ReglaDeLotes.ControlEfectivo(articuloPorId[i.IdArticulo].ControlaLote, lotesHabilitado))
            .ToList();

        // dto-contract-honesty: un IdLote persistido en una línea SIN lote efectivo (el módulo se
        // apagó, o el artículo dejó de controlar lote, entre el borrador y el emitir) no tiene
        // destino — se rechaza en vez de ignorarse en silencio, mismo criterio que el checkout.
        var idsConLoteEfectivo = lineasConLote.Select(x => x.Id).ToHashSet();
        foreach (var item in items)
        {
            if (!idsConLoteEfectivo.Contains(item.Id) && item.IdLote is not null)
            {
                throw new ErrorDominio(
                    "lote_invalido",
                    $"El artículo {item.IdArticulo} no tiene lote efectivo; no admite idLote.",
                    400);
            }
        }

        if (lineasConLote.Count == 0)
        {
            return resultado;
        }

        var idsArticuloConLote = lineasConLote.Select(i => i.IdArticulo).Distinct().ToList();
        var idsLotePedidos = lineasConLote
            .Where(i => i.IdLote is not null)
            .Select(i => i.IdLote!.Value)
            .Distinct()
            .ToList();

        var saldos = await servicioDeLotes.LeerSaldosAsync(idPuntoVenta, idsArticuloConLote, idsLotePedidos, ct);
        var saldosPorArticulo = saldos.ToLookup(s => s.IdArticulo);

        // Honestidad documental: mismo "hoy" UTC-naive interino que el checkout (decisión 10,
        // mutation target 47) — jamás la zona del punto de venta acá, aunque el vencimiento de un
        // presupuesto SÍ la use.
        var hoy = DateOnly.FromDateTime(momento.UtcDateTime);

        foreach (var item in lineasConLote)
        {
            var saldosDelArticulo = saldosPorArticulo[item.IdArticulo].ToList();

            SaldoDeLote loteResuelto;
            if (item.IdLote is { } idLote)
            {
                // Pick explícito, ya persistido desde el borrador — re-validado contra el saldo
                // VIGENTE (pudo haberse dado de baja entre el borrador y el emitir).
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
                var conexionParaLotes = await ObtenerConexionAbiertaAsync(ct);
                var idSinIdentificar = await ServicioDeLotes.ResolverSinIdentificarAsync(
                    conexionParaLotes, transaccion: null, idTenant, item.IdArticulo, momento, ct);

                loteResuelto = new SaldoDeLote(
                    item.IdArticulo, idSinIdentificar, ReglaDeLotes.CodigoSinIdentificar,
                    EsSinIdentificar: true, FechaVencimiento: null, Cantidad: 0m);
            }

            resultado[item.Id] = loteResuelto;
        }

        return resultado;
    }

    private async Task<RemitoDetalle> EjecutarEmisionAsync(
        int id, int idTenant, int idPuntoVenta, int idEmpleado, long numero, DateTimeOffset momento,
        IReadOnlyList<ItemRemito> items, IReadOnlyDictionary<int, Articulo> articuloPorId,
        IReadOnlyDictionary<int, SaldoDeLote> loteFinalPorItem, CancellationToken ct)
    {
        await using var transaccion = await db.Database.BeginTransactionAsync(ct);

        var conexion = await ObtenerConexionAbiertaAsync(ct);
        var transaccionCruda = db.Database.CurrentTransaction?.GetDbTransaction();

        // task 5.3: UPDATE remitos ... WHERE estado='borrador' AND id_punto_venta=$pv RETURNING
        // numero — mismo criterio (0 filas ⇒ reclasificar) que EnviarAsync de presupuestos.
        var numeroAsignado = await EmitirHeaderAsync(
            conexion, transaccionCruda, id, idTenant, idPuntoVenta, numero, momento, ct);
        if (numeroAsignado is null)
        {
            var existe = await db.Remitos.AsNoTracking().AnyAsync(r => r.Id == id, ct);
            if (!existe)
            {
                throw ErrorDominio.NoEncontrado($"No existe el remito {id}.");
            }

            throw new ErrorDominio(
                "remito_ya_emitido", "El remito ya no está en borrador en ese punto de venta.", 409);
        }

        // task 5.6: EL CUARTO WRITE SITE — orden ascendente (id_articulo, id_lote NULLS FIRST),
        // implementado independiente (design decisión 8): sus propios statements crudos, sin
        // compartir helper con ServicioDeVentas/ServicioDeCompras/ServicioDeStock.
        var itemsOrdenados = items
            .Select(item => (
                Item: item,
                IdLoteFinal: loteFinalPorItem.TryGetValue(item.Id, out var saldo) ? saldo.IdLote : (int?)null))
            .OrderBy(x => x.Item.IdArticulo)
            .ThenBy(x => x.IdLoteFinal.HasValue)
            .ThenBy(x => x.IdLoteFinal ?? 0)
            .ToList();

        foreach (var (item, idLoteFinal) in itemsOrdenados)
        {
            var articulo = articuloPorId[item.IdArticulo];

            // task 5.5: freeze de costo/lote — costo_unitario sale de HOY (articulo.CostoNominal),
            // nunca del momento de creación del borrador (design.md:292, mismo criterio que
            // MaterializarItemsDesdePresupuesto's mutation target 29).
            await CongelarItemAsync(
                conexion, transaccionCruda, item.Id, idTenant, idLoteFinal, articulo.CostoNominal, ct);

            var delta = -item.Cantidad;

            await InsertarMovimientoStockAsync(
                conexion, transaccionCruda, idTenant, item.IdArticulo, idPuntoVenta, delta,
                MotivoStock.Remito, id, idEmpleado, momento, idLoteFinal, ct);

            await UpsertStockAsync(conexion, transaccionCruda, idTenant, item.IdArticulo, idPuntoVenta, delta, ct);

            if (idLoteFinal is { } idLote)
            {
                await UpsertStockLoteAsync(
                    conexion, transaccionCruda, idTenant, item.IdArticulo, idPuntoVenta, idLote, delta, ct);
            }
        }

        await transaccion.CommitAsync(ct);

        return await ObtenerDetalleAsync(id, ct);
    }

    // ---- anular: sin coupling con facturado, sin chequeo de negativo (decisión 9) -------------------

    /// <summary>design: Transactions — "ANULAR REMITO", tasks 5.8-5.9. Un único <c>UPDATE …
    /// RETURNING</c> admite <c>borrador</c> Y <c>emitido</c> (spec: "MUST be allowed for borrador or
    /// emitido") — para un <c>borrador</c> nunca se escribió ningún <c>movimientos_stock</c>, así
    /// que el loop de reversa de abajo lee una lista vacía y no hace nada, sin ninguna rama especial
    /// (mismo criterio "estructural, no una bandera" que el itemless <c>RC</c>/<c>TXR</c>). 0 filas
    /// reclasifica en 404 / 409 <c>remito_facturado</c> / 409 <c>remito_ya_anulado</c> (OD8/T2, task
    /// 5.9 — el escenario de doble-anulación ausente de <c>remitos/spec.md</c>).</summary>
    public async Task<RemitoDetalle> AnularAsync(int id, CancellationToken ct = default)
    {
        var idTenant = ExigirTenantDeLaSesion();
        var idEmpleado = contexto.UsuarioId;
        var momento = reloj.Ahora;

        var estrategia = FabricaDeEstrategiaSinReintento.CrearEstrategiaSinReintento(db);
        return await estrategia.ExecuteAsync(async () =>
            await EjecutarAnulacionAsync(id, idTenant, idEmpleado, momento, ct));
    }

    private async Task<RemitoDetalle> EjecutarAnulacionAsync(
        int id, int idTenant, int idEmpleado, DateTimeOffset momento, CancellationToken ct)
    {
        await using var transaccion = await db.Database.BeginTransactionAsync(ct);

        var conexion = await ObtenerConexionAbiertaAsync(ct);
        var transaccionCruda = db.Database.CurrentTransaction?.GetDbTransaction();

        var anulado = await MarcarAnuladoAsync(conexion, transaccionCruda, id, idTenant, momento, ct);
        if (!anulado)
        {
            var actual = await db.Remitos.AsNoTracking().FirstOrDefaultAsync(r => r.Id == id, ct);
            if (actual is null)
            {
                throw ErrorDominio.NoEncontrado($"No existe el remito {id}.");
            }

            if (actual.Estado == EstadoRemito.Facturado)
            {
                throw new ErrorDominio(
                    "remito_facturado", "El remito ya está facturado; no puede anularse directamente.", 409);
            }

            if (actual.Estado == EstadoRemito.Anulado)
            {
                throw new ErrorDominio("remito_ya_anulado", "El remito ya está anulado.", 409);
            }

            // Defensa en profundidad: el UPDATE guardado de arriba ya evaluó el mismo predicado
            // que este re-chequeo — llegar acá es un invariante roto, nunca un caso de negocio
            // alcanzable (mismo criterio que ExigirCausaDelRechazoAsync).
            throw new InvalidOperationException(
                $"El remito {id} no matcheó el UPDATE guardado pero tampoco una causa conocida de rechazo " +
                $"(estado leído: {actual.Estado}).");
        }

        // task 5.8/design decisión 9: movimientos ORIGINALES del ledger (motivo = remito), NUNCA
        // re-derivados de items_remito — orden ascendente (id_articulo, id_lote), mismo criterio
        // anti-deadlock que EmitirAsync (por consistencia de convención, no por necesidad estricta
        // acá). SIN chequeo de negativo: un remito decrementa, su reversa siempre suma
        // (ServicioDeVentas.cs:1130-1135 posture verbatim, tensión T8).
        var movimientosOriginales = await db.MovimientosStock
            .Where(m => m.IdRemito == id && m.Motivo == MotivoStock.Remito)
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

        await transaccion.CommitAsync(ct);

        return await ObtenerDetalleAsync(id, ct);
    }

    // ---- statements crudos: header (mismo criterio que EnviarHeaderAsync de presupuestos) ----------

    private static async Task<bool> BloquearBorradorAsync(
        DbConnection conexion, DbTransaction? transaccion, int id, int idTenant, CancellationToken ct)
    {
        await using var comando = conexion.CreateCommand();
        comando.Transaction = transaccion;
        comando.CommandText =
            "SELECT 1 FROM remitos " +
            "WHERE id_remito = $1 AND id_tenant = $2 AND estado = 'borrador'::estado_remito " +
            "FOR UPDATE";

        ParametrosDeComando.Agregar(comando, id);
        ParametrosDeComando.Agregar(comando, idTenant);

        var resultado = await comando.ExecuteScalarAsync(ct);
        return resultado is not null;
    }

    /// <summary>design.md:288-291, mutation target 44: pinea <c>estado='borrador'</c> Y
    /// <c>id_punto_venta=$pv</c> — sin el segundo conjunto, un <c>PUT</c> concurrente que mueve el
    /// remito a otro punto de venta haría aterrizar el número en la serie equivocada.</summary>
    private static async Task<long?> EmitirHeaderAsync(
        DbConnection conexion, DbTransaction? transaccion, int id, int idTenant, int idPuntoVenta, long numero,
        DateTimeOffset momento, CancellationToken ct)
    {
        await using var comando = conexion.CreateCommand();
        comando.Transaction = transaccion;
        comando.CommandText =
            "UPDATE remitos SET numero = $1, fecha_salida = $2, estado = 'emitido'::estado_remito, updated_at = $2 " +
            "WHERE id_remito = $3 AND id_tenant = $4 AND estado = 'borrador'::estado_remito " +
            "AND id_punto_venta = $5 " +
            "RETURNING numero";

        ParametrosDeComando.Agregar(comando, numero);
        ParametrosDeComando.Agregar(comando, momento);
        ParametrosDeComando.Agregar(comando, id);
        ParametrosDeComando.Agregar(comando, idTenant);
        ParametrosDeComando.Agregar(comando, idPuntoVenta);

        await using var lector = await comando.ExecuteReaderAsync(ct);
        if (!await lector.ReadAsync(ct))
        {
            return null;
        }

        return lector.IsDBNull(0) ? null : lector.GetInt64(0);
    }

    /// <summary>design.md:292, task 5.5: congela <c>id_lote</c>/<c>costo_unitario</c>/
    /// <c>costo_es_estimado</c> — <c>costo_es_estimado</c> siempre <c>false</c>, mismo criterio que
    /// <c>ServicioDeVentas.MaterializarItems</c> (el costo puede ser <c>NULL</c> — "desconocido" —
    /// sin que eso lo vuelva "estimado"; <c>ck_items_remito_estimado_con_costo</c> lo admite: la
    /// CHECK solo exige costo no-nulo cuando el flag está prendido).</summary>
    private static async Task CongelarItemAsync(
        DbConnection conexion, DbTransaction? transaccion, int idItem, int idTenant, int? idLote,
        decimal? costoUnitario, CancellationToken ct)
    {
        await using var comando = conexion.CreateCommand();
        comando.Transaction = transaccion;
        comando.CommandText =
            "UPDATE items_remito SET id_lote = $1, costo_unitario = $2, costo_es_estimado = false " +
            "WHERE id_item = $3 AND id_tenant = $4";

        ParametrosDeComando.AgregarNulo(comando, idLote);
        ParametrosDeComando.AgregarNulo(comando, costoUnitario);
        ParametrosDeComando.Agregar(comando, idItem);
        ParametrosDeComando.Agregar(comando, idTenant);

        await comando.ExecuteNonQueryAsync(ct);
    }

    /// <summary>design.md:301-302, mutation targets 45-46: single-statement, admite <c>borrador</c>
    /// Y <c>emitido</c> — <c>facturado</c>/<c>anulado</c> quedan afuera del <c>IN</c> a propósito
    /// (spec: "facturado MUST be rejected with 409"; OD8/T2: doble-anulación también 409, distinguido
    /// del 409 de facturado por el estado leído en la reclasificación del llamador).</summary>
    private static async Task<bool> MarcarAnuladoAsync(
        DbConnection conexion, DbTransaction? transaccion, int id, int idTenant, DateTimeOffset momento,
        CancellationToken ct)
    {
        await using var comando = conexion.CreateCommand();
        comando.Transaction = transaccion;
        comando.CommandText =
            "UPDATE remitos SET estado = 'anulado'::estado_remito, updated_at = $1 " +
            "WHERE id_remito = $2 AND id_tenant = $3 " +
            "AND estado IN ('borrador'::estado_remito, 'emitido'::estado_remito) " +
            "RETURNING estado";

        ParametrosDeComando.Agregar(comando, momento);
        ParametrosDeComando.Agregar(comando, id);
        ParametrosDeComando.Agregar(comando, idTenant);

        var resultado = await comando.ExecuteScalarAsync(ct);
        return resultado is not null;
    }

    // ---- statements crudos: EL CUARTO WRITE SITE (design decisión 8 — deliberadamente NO ---------
    // ---- comparte código con ServicioDeVentas/ServicioDeCompras/ServicioDeStock) -------------------

    /// <summary>design.md:294, mutation target 42: <c>motivo</c> y <c>id_remito</c> (NUNCA
    /// <c>id_comprobante_venta</c>) — el documento del cuarto write site (proposal §H). Implementado
    /// independiente de <c>ServicioDeVentas.InsertarMovimientoStockAsync</c> — mismo shape SQL, otra
    /// clase, otro archivo (la duplicación es el contrato de <c>stock/spec.md:178-189</c>).</summary>
    private static async Task InsertarMovimientoStockAsync(
        DbConnection conexion, DbTransaction? transaccion, int idTenant, int idArticulo, int idPuntoVenta,
        decimal cantidad, MotivoStock motivo, int idRemito, int idEmpleado, DateTimeOffset creadoEl,
        int? idLote, CancellationToken ct)
    {
        await using var comando = conexion.CreateCommand();
        comando.Transaction = transaccion;
        comando.CommandText =
            "INSERT INTO movimientos_stock " +
            "(id_tenant, id_articulo, id_punto_venta, cantidad, motivo, id_remito, id_empleado, creado_el, id_lote) " +
            "VALUES ($1, $2, $3, $4, $5, $6, $7, $8, $9)";

        ParametrosDeComando.Agregar(comando, idTenant);
        ParametrosDeComando.Agregar(comando, idArticulo);
        ParametrosDeComando.Agregar(comando, idPuntoVenta);
        ParametrosDeComando.Agregar(comando, cantidad);
        ParametrosDeComando.Agregar(comando, motivo);
        ParametrosDeComando.Agregar(comando, idRemito);
        ParametrosDeComando.Agregar(comando, idEmpleado);
        ParametrosDeComando.Agregar(comando, creadoEl);
        ParametrosDeComando.AgregarNulo(comando, idLote);

        await comando.ExecuteNonQueryAsync(ct);
    }

    /// <summary>design decisión 8: el único statement que toca <c>stock</c> desde este write site —
    /// su propio row lock (implícito en el <c>INSERT ... ON CONFLICT</c>) reemplaza al advisory
    /// lock, mismo mecanismo que write site 1, código PROPIO.</summary>
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

    /// <summary>Sin chequeo de negativo — decisión 9: una emisión siempre resta (nunca puede dejar
    /// el saldo negativo por construcción, la reversa de <see cref="AnularAsync"/> siempre suma).
    /// </summary>
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

    // ---- resolución de precio (mismo criterio que ServicioDePresupuestos, sin signo) ---------------

    private async Task<(IReadOnlyList<LineaMaterializada> Lineas, TotalesCalculados Totales)> ResolverYMaterializarAsync(
        IReadOnlyList<LineaDeRemito> lineas, int idTenant, int idEmpresa, int idListaPrecio, DateTimeOffset momento,
        CancellationToken ct)
    {
        if (lineas.Count == 0)
        {
            return (Array.Empty<LineaMaterializada>(), CalculadorDeTotales.Calcular([]));
        }

        // Backstop map FK 22 (design.md:392): "Yes (item lines) — Same pre-check shape ... +
        // generic mapping" — un idLote explícito se valida ACÁ, antes de escribir nada
        // (dto-contract-honesty regla 1: el campo tiene que tener un destino real, y ese destino
        // solo es válido si el lote existe).
        foreach (var linea in lineas)
        {
            if (linea.IdLote is { } idLote
                && !await db.Lotes.AsNoTracking().AnyAsync(l => l.Id == idLote && l.IdArticulo == linea.IdArticulo, ct))
            {
                throw new ErrorDominio(
                    "lote_invalido",
                    $"El lote {idLote} no existe o no pertenece al artículo {linea.IdArticulo}.",
                    400);
            }
        }

        var lineasDeResolucion = lineas
            .Select(l => new LineaDeResolucion(l.IdArticulo, idEmpresa, idListaPrecio, l.Cantidad))
            .ToList();
        var resolucion = await servicioDeOfertas.ResolverAsync(lineasDeResolucion, momento, ct);

        var idsArticulo = lineas.Select(l => l.IdArticulo).Distinct().ToList();
        var articuloPorId = await db.Articulos
            .Where(a => idsArticulo.Contains(a.Id))
            .ToDictionaryAsync(a => a.Id, ct);

        var idsAlicuota = articuloPorId.Values.Select(a => a.IdAlicuotaIva).Distinct().ToList();
        var porcentajePorAlicuota = await db.AlicuotasIva
            .Where(a => idsAlicuota.Contains(a.Id))
            .ToDictionaryAsync(a => a.Id, a => a.Porcentaje, ct);

        return MaterializarLineas(lineas, resolucion, articuloPorId, porcentajePorAlicuota, idListaPrecio);
    }

    private static (IReadOnlyList<LineaMaterializada> Lineas, TotalesCalculados Totales) MaterializarLineas(
        IReadOnlyList<LineaDeRemito> lineas,
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

            lineasParaCalcular.Add(new LineaParaCalcular(
                lineas[i].Cantidad, resultado.PrecioOriginal.Value, resultado.DescuentoUnitario));
        }

        var totales = CalculadorDeTotales.Calcular(lineasParaCalcular);

        var resultadoFinal = new List<LineaMaterializada>(lineas.Count);
        for (var i = 0; i < lineas.Count; i++)
        {
            var linea = lineas[i];
            var resultado = resolucion[i];
            var calculado = totales.Items[i];
            var articulo = articuloPorId[linea.IdArticulo];

            var idOferta = resultado.Aplicadas.Count > 0 ? resultado.Aplicadas[0].IdOferta : (int?)null;

            resultadoFinal.Add(new LineaMaterializada(
                articulo.Id, articulo.Nombre, calculado.Cantidad, calculado.PrecioUnitario, calculado.Descuento,
                calculado.Total, idListaPrecio, idOferta, articulo.IdAlicuotaIva,
                porcentajePorAlicuota[articulo.IdAlicuotaIva], linea.IdLote));
        }

        return (resultadoFinal, totales);
    }

    private static List<ItemRemito> ConstruirItems(
        int idRemito, int idTenant, DateTimeOffset momento, IReadOnlyList<LineaMaterializada> lineas)
    {
        var resultado = new List<ItemRemito>(lineas.Count);
        var orden = 1;

        foreach (var linea in lineas)
        {
            resultado.Add(new ItemRemito
            {
                IdTenant = idTenant,
                IdRemito = idRemito,
                Orden = orden++,
                IdArticulo = linea.IdArticulo,
                Descripcion = linea.Descripcion,
                Cantidad = linea.Cantidad,
                PrecioUnitario = linea.PrecioUnitario,
                Descuento = linea.Descuento,
                Total = linea.Total,
                IdListaPrecio = linea.IdListaPrecio,
                IdOferta = linea.IdOferta,
                IdAlicuotaIva = linea.IdAlicuotaIva,
                PorcentajeIva = linea.PorcentajeIva,
                CostoUnitario = null,
                CostoEsEstimado = false,
                IdLote = linea.IdLoteExplicito,
                CreatedAt = momento,
                UpdatedAt = momento
            });
        }

        return resultado;
    }

    private static void ExigirCantidadesValidas(IReadOnlyList<LineaDeRemito> lineas)
    {
        foreach (var linea in lineas)
        {
            if (linea.Cantidad <= 0)
            {
                throw new ErrorDominio(
                    "cantidad_de_linea_invalida", "La cantidad de una línea de remito tiene que ser positiva.", 400);
            }
        }
    }

    // ---- parámetro lotes_habilitado (mismo criterio que ServicioDeVentas.ResolverParametrosDeVentaAsync) --

    private async Task<bool> ResolverLotesHabilitadoAsync(int idEmpresa, int idPuntoVenta, CancellationToken ct)
    {
        var clave = ParametroConocido.LotesHabilitado.Clave;

        var candidatos = await db.Parametros
            .Where(p => p.Clave == clave && p.IdEmpresa == idEmpresa
                && (p.IdPuntoVenta == null || p.IdPuntoVenta == idPuntoVenta))
            .ToListAsync(ct);

        var resuelto = ResolucionDeParametros.Resolver(clave, candidatos, idPuntoVenta);
        return JsonSerializer.Deserialize<bool>(resuelto);
    }

    // ---- resolución de contexto (fuera de transacción, resolvers PRIVADOS PROPIOS — OD9) -----------

    private async Task<PuntoVenta> ResolverPuntoVentaAsync(int idPuntoVenta, CancellationToken ct) =>
        await db.PuntosVenta.FirstOrDefaultAsync(pv => pv.Id == idPuntoVenta, ct)
            ?? throw ErrorDominio.NoEncontrado($"No existe el punto de venta {idPuntoVenta}.");

    private async Task<Cliente> ResolverClienteAsync(int? idCliente, CancellationToken ct)
    {
        if (idCliente is { } id)
        {
            return await db.Clientes.FirstOrDefaultAsync(c => c.Id == id, ct)
                ?? throw ErrorDominio.NoEncontrado($"No existe el cliente {id}.");
        }

        return await db.Clientes.FirstOrDefaultAsync(c => c.Numero == ReglaDeClientes.NumeroConsumidorFinal, ct)
            ?? throw new InvalidOperationException("El tenant actual no tiene un Consumidor Final sembrado.");
    }

    // ---- proyección (task 5.19: rules 12b/12c — todo campo posicional, valores distintos) ---------

    private static RemitoDetalle ProyectarDetalle(Remito remito, IReadOnlyList<ItemRemito> items) =>
        new(
            remito.Id,
            remito.IdPuntoVenta,
            remito.IdCliente,
            remito.IdEmpleado,
            remito.Numero,
            remito.Numero is { } n ? NumeroDeComprobante.Formatear(remito.IdPuntoVenta, n) : null,
            remito.FechaEmision,
            remito.FechaSalida,
            remito.DireccionEntrega,
            remito.Observaciones,
            remito.Subtotal,
            remito.DescuentoTotal,
            remito.Total,
            remito.Estado,
            remito.IdComprobanteVenta,
            items
                .OrderBy(i => i.Orden)
                .Select(i => new ItemDeRemito(
                    i.Orden, i.IdArticulo, i.Descripcion, i.Cantidad, i.PrecioUnitario, i.Descuento, i.Total,
                    i.IdListaPrecio, i.IdOferta, i.IdAlicuotaIva, i.PorcentajeIva, i.CostoUnitario,
                    i.CostoEsEstimado, i.IdLote))
                .ToList());

    private static RemitoListado ProyectarListado(Remito remito) =>
        new(
            remito.Id,
            remito.IdPuntoVenta,
            remito.IdCliente,
            remito.Numero,
            remito.Numero is { } n ? NumeroDeComprobante.Formatear(remito.IdPuntoVenta, n) : null,
            remito.FechaEmision,
            remito.Total,
            remito.Estado,
            remito.IdComprobanteVenta);

    private static string? NormalizarOpcional(string? valor)
    {
        var limpio = valor?.Trim();
        return string.IsNullOrEmpty(limpio) ? null : limpio;
    }

    private int ExigirTenantDeLaSesion() =>
        contexto.IdTenant
            ?? throw new InvalidOperationException(
                "ServicioDeRemitos requiere un actor de tenant; OperacionDePos no admite plataforma.");

    private readonly record struct LineaMaterializada(
        int IdArticulo,
        string Descripcion,
        decimal Cantidad,
        decimal PrecioUnitario,
        decimal Descuento,
        decimal Total,
        int IdListaPrecio,
        int? IdOferta,
        int IdAlicuotaIva,
        decimal PorcentajeIva,
        int? IdLoteExplicito);
}
