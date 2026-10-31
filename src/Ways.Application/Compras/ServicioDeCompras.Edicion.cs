using System.Data.Common;
using System.Globalization;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Storage;
using Ways.Application.Abstracciones;
using Ways.Application.Auditoria;
using Ways.Application.CuentaCorriente;
using Ways.Application.Familias;
using Ways.Application.Pagos;
using Ways.Application.Stock;
using Ways.Domain.Auditoria;
using Ways.Domain.Common;
using Ways.Domain.Compras;
using Ways.Domain.CuentaCorriente;
using Ways.Domain.Pagos;
using Ways.Domain.Stock;

namespace Ways.Application.Compras;

/// <summary>
/// E6 — edición de una compra confirmada (<c>PUT /api/compras/{id}</c>). El stock y la cuenta corriente se corrigen por
/// DIFERENCIA contra lo que la compra ya asentó (<see cref="CalculadorDeEdicionDeCompra"/>), con movimientos nuevos de
/// signo: los libros nunca se reescriben.
///
/// <para><b>Orden de locks</b> (el global del repo): membresía de familias COMPARTIDO (primera sentencia, aunque esta
/// slice no toca costos; la slice de costos lo necesita y moverlo después reabriría el orden) → encabezado de la compra
/// <c>FOR UPDATE … WHERE estado='confirmada'</c> → órdenes de compra afectadas, ascendentes por id, <c>FOR UPDATE</c> →
/// pagos cuyas aplicaciones se recortan o liberan, o cuyo vínculo con la compra se corta (E6-S3), <c>FOR UPDATE</c>,
/// ascendentes por id →
/// lotes (ascendente por artículo y código RESUELTO, ordinal: <see cref="ServicioDeLotes.CodigoResuelto"/>, igual que la confirmación) → stock (ascendente por artículo y punto de venta, agregado antes que lotes) →
/// artículos con costo a reescribir (E6-S4: ascendentes por id, <c>FOR NO KEY UPDATE</c>, como la confirmación) →
/// escritura de encabezado, líneas, alícuotas y percepciones por EF → códigos de proveedor (sin lock de fila) →
/// re-proyección de las órdenes → proveedores,
/// ascendentes por id, ÚLTIMO lock de fila, y su asiento de cuenta corriente.</para>
/// </summary>
public partial class ServicioDeCompras
{
    /// <summary>Despacha el <c>PUT</c> según el estado: un borrador reemplaza su set completo
    /// (<see cref="ActualizarBorradorAsync"/>, sin cambios), una confirmada se edita por diferencia
    /// (<see cref="ActualizarConfirmadaAsync"/>) y una anulada no se edita. El 404 es lo primero que se observa: la
    /// lectura del estado es una proyección escalar sin entidad rastreada, así que no ocupa el identity map.</summary>
    public async Task<CompraDetalle> ActualizarAsync(int id, SolicitudDeCompra solicitud, CancellationToken ct = default)
    {
        var estado = await db.ComprobantesCompra
            .AsNoTracking()
            .Where(c => c.Id == id)
            .Select(c => (EstadoCompra?)c.Estado)
            .FirstOrDefaultAsync(ct);

        return estado switch
        {
            null => throw ErrorDominio.NoEncontrado($"No existe la compra {id}."),
            EstadoCompra.Borrador => await ActualizarBorradorAsync(id, solicitud, ct),
            EstadoCompra.Confirmada => await ActualizarConfirmadaAsync(id, solicitud, ct),
            _ => throw new ErrorDominio("compra_no_editable", "Una compra anulada no puede editarse.", 409)
        };
    }

    /// <summary>Edición de una compra confirmada. Forma (b) de <c>ef-retry-safe-writes</c>: una sola transacción sin
    /// reintento, porque repetir la edición tras un commit ambiguo asentaría dos veces la diferencia de stock y de cuenta;
    /// ese residual lo cubre el 503 <c>resultado_incierto</c> central de <c>ManejadorDeErrores</c>.</summary>
    public async Task<CompraDetalle> ActualizarConfirmadaAsync(int id, SolicitudDeCompra solicitud, CancellationToken ct = default)
    {
        var idTenant = ExigirTenantDeLaSesion();
        var idEmpleado = contexto.UsuarioId;
        var momento = reloj.Ahora;

        ValidarLineasDeConcepto(solicitud.Items);
        var codigosDeProveedor = NormalizarCodigosDeProveedor(solicitud.Items);

        var (tipo, _, puntoVenta, _, porcentajePorAlicuota, margenes, redondeoDeSugerencia) = await ResolverContextoAsync(solicitud, ct);
        var numeroExterno = NormalizarNumeroExterno(tipo, solicitud.NumeroExterno);

        // Una confirmada no puede quedar incompleta (ck_comprobantes_compra_confirmada_completa) ni sin líneas: se
        // rechaza acá, antes de tomar ningún lock, con los mismos códigos de la confirmación.
        if (numeroExterno is null)
        {
            throw new ErrorDominio(
                "compra_numero_externo_requerido",
                "La compra necesita un número de comprobante del proveedor para confirmarse.", 400);
        }

        if (solicitud.FechaComprobante is null)
        {
            throw new ErrorDominio(
                "compra_incompleta_para_confirmar", "La compra necesita una fecha de comprobante para confirmarse.", 400);
        }

        if (solicitud.Items.Count == 0)
        {
            throw new ErrorDominio("compra_sin_items", "La compra no tiene items para confirmar.", 400);
        }

        var discriminaIva = ReglaDeDiscriminacionDeIva.Resolver(tipo.RegistraLibroIva, tipo.DiscriminaIva, solicitud.DiscriminaIva);
        var percepciones = ConvertirPercepciones(solicitud.Percepciones);
        ReglaDePercepciones.Validar(tipo.RegistraLibroIva, discriminaIva, percepciones);
        var (lineas, calculada) = Calcular(
            solicitud.Items, discriminaIva, porcentajePorAlicuota, margenes, ConvertirIvaImpreso(solicitud.IvaImpreso),
            solicitud.PreciosIncluyenIva, percepciones, redondeoDeSugerencia);

        // La orden pedida tiene que existir: su 404 sale acá, sin lock, porque el lock exclusivo de abajo asume que la fila
        // existe (la FK lo garantiza una vez ligada).
        if (solicitud.IdOrdenCompra is { } idOrdenPedida
            && !await db.OrdenesCompra.AsNoTracking().AnyAsync(o => o.Id == idOrdenPedida, ct))
        {
            throw ErrorDominio.NoEncontrado($"No existe la orden de compra {idOrdenPedida}.");
        }

        var estrategia = FabricaDeEstrategiaSinReintento.CrearEstrategiaSinReintento(db);
        return await estrategia.ExecuteAsync(async () =>
            await EjecutarEdicionConfirmadaAsync(
                id, idTenant, idEmpleado, solicitud, numeroExterno, discriminaIva, lineas, calculada, codigosDeProveedor,
                puntoVenta.IdEmpresa, momento, ct));
    }

    private async Task<CompraDetalle> EjecutarEdicionConfirmadaAsync(
        int id, int idTenant, int idEmpleado, SolicitudDeCompra solicitud, string numeroExterno, bool discriminaIva,
        IReadOnlyList<LineaDeCompra> lineas, CompraCalculada calculada, IReadOnlyList<string?> codigosDeProveedor,
        int idEmpresaNueva, DateTimeOffset momento, CancellationToken ct)
    {
        await using var transaccion = await db.Database.BeginTransactionAsync(ct);

        var conexion = await ObtenerConexionAbiertaAsync(ct);
        var transaccionCruda = db.Database.CurrentTransaction?.GetDbTransaction();

        // 0. Lock de membresía de familias, COMPARTIDO y PRIMERA sentencia de la transacción (doc 10 §3): ver el orden
        // global en el resumen de la clase.
        await LockDeMembresiaDeFamilias.TomarCompartidoAsync(conexion, transaccionCruda, idTenant, ct);

        // 1. Encabezado FOR UPDATE … WHERE estado='confirmada': el predicado hace imposible editar una compra que otro
        // anuló entre el despacho y este lock. 0 filas: o la compra desapareció (404) o dejó de estar confirmada (409).
        if (!await BloquearConfirmadaAsync(conexion, transaccionCruda, id, idTenant, ct))
        {
            if (!await db.ComprobantesCompra.AsNoTracking().AnyAsync(c => c.Id == id, ct))
            {
                throw ErrorDominio.NoEncontrado($"No existe la compra {id}.");
            }

            throw new ErrorDominio(
                "compra_cambio_concurrente",
                "La compra cambió de estado mientras se editaba. Recargala y volvé a intentar.", 409);
        }

        // La ÚNICA lectura del encabezado (single-read-under-lock): nace después del lock, así que el estado anterior que
        // se audita y se compara es el que esta transacción vio bajo él.
        var comprobante = await db.ComprobantesCompra.FirstAsync(c => c.Id == id, ct);
        var itemsExistentes = await db.ItemsComprobanteCompra
            .Where(i => i.IdComprobanteCompra == id)
            .OrderBy(i => i.Orden)
            .ToListAsync(ct);

        var anterior = new ResumenDeCompraEditada(
            comprobante.IdProveedor, comprobante.IdPuntoVenta, comprobante.NumeroExterno, comprobante.IdOrdenCompra,
            comprobante.Total,
            [.. itemsExistentes.Select(i => new LineaAuditadaDeCompra(i.IdArticulo, i.Cantidad, i.Total))]);
        var idOrdenAnterior = comprobante.IdOrdenCompra;
        var cambiaProveedor = comprobante.IdProveedor != solicitud.IdProveedor;
        var cambiaPuntoVenta = comprobante.IdPuntoVenta != solicitud.IdPuntoVenta;

        // 2. Órdenes de compra afectadas, ascendentes por id y con lock exclusivo: la que la compra deja y la que toma. La
        // ligadura de la nueva (existe, no anulada, mismo proveedor y punto de venta) se verifica solo cuando la ligadura
        // cambia o cambia el proveedor o el punto de venta: una orden que se anuló después de ligarse no impide editar.
        var ordenesAfectadas = new[] { idOrdenAnterior, solicitud.IdOrdenCompra }
            .Where(o => o is not null)
            .Select(o => o!.Value)
            .Distinct()
            .Order()
            .ToList();

        foreach (var idOrden in ordenesAfectadas)
        {
            await EscriturasDeOrdenDeCompra.BloquearAsync(conexion, transaccionCruda, idTenant, idOrden, ct);

            if (idOrden == solicitud.IdOrdenCompra && (idOrden != idOrdenAnterior || cambiaProveedor || cambiaPuntoVenta))
            {
                await ExigirOrdenLigableAsync(idOrden, idTenant, solicitud.IdProveedor, solicitud.IdPuntoVenta, ct);
            }
        }

        // 3. Pagos de la compra (E6-S3). Se planea con lecturas posteriores al lock del encabezado, que serializa a todo
        // escritor de aplicaciones de la compra (todos lo toman antes que el pago), y se bloquean los pagos afectados
        // ascendentes por id: después de las órdenes y ANTES de lotes y stock, antes del proveedor, que sigue último.
        var cambiaEmpresa = cambiaPuntoVenta && await db.PuntosVenta
            .AsNoTracking()
            .IgnoreQueryFilters(["BajaLogica"])
            .Where(p => p.Id == comprobante.IdPuntoVenta || p.Id == solicitud.IdPuntoVenta)
            .Select(p => p.IdEmpresa)
            .Distinct()
            .CountAsync(ct) > 1;
        var plan = await PlanearElTratamientoDePagosAsync(
            id, comprobante.Total, calculada.Total, cambiaProveedor || cambiaEmpresa, ct);

        await BloquearPagosDeLaEdicionAsync(conexion, transaccionCruda, idTenant, plan.IdsDePagosAfectados, ct);
        var desvinculados = await AplicarElTratamientoDePagosAsync(
            conexion, transaccionCruda, id, idTenant, plan, anterior.IdPuntoVenta, momento, ct);

        // 3.b Diferencia de cuenta corriente contra la suma de los asientos de la compra. Sin asiento propio (compra
        // anterior al libro de proveedores) el total del encabezado hace de asiento, pero un asiento nuevo dejaría a la
        // anulación revirtiendo solo la diferencia: esa edición se rechaza, antes de escribir nada.
        var asientosDeCuenta = (await db.MovimientosCuentaCorrienteProveedor
                .Where(m => m.IdComprobanteCompra == id && m.Tipo == TipoMovimientoCcProveedor.Compra)
                .Select(m => new { m.IdProveedor, m.Importe })
                .ToListAsync(ct))
            .Select(m => new AsientoDeCuentaDeCompra(m.IdProveedor, m.Importe))
            .ToList();
        var sinAsiento = asientosDeCuenta.Count == 0;
        var diferenciasDeCuenta = CalculadorDeEdicionDeCompra.DiferenciasDeCuentaCorriente(
            sinAsiento ? [new AsientoDeCuentaDeCompra(anterior.IdProveedor, anterior.Total)] : asientosDeCuenta,
            solicitud.IdProveedor, calculada.Total);

        if (sinAsiento && diferenciasDeCuenta.Count > 0)
        {
            throw new ErrorDominio(
                "compra_edicion_sin_asiento_de_cuenta",
                "La compra es anterior al libro de cuenta corriente de proveedores y no tiene asiento propio: no puede " +
                "cambiar su total ni su proveedor.",
                409);
        }

        // 4. Lotes (design decisión 3 de la etapa 12: lotes ANTES que stock). La línea que conserva artículo, código y
        // vencimiento conserva su lote sin volver a verificarse; las nuevas o cambiadas se resuelven con las mismas
        // exigencias de la confirmación, en orden ascendente (artículo, código).
        var itemsNuevos = MaterializarItems(id, idTenant, lineas, calculada, solicitud.Items, codigosDeProveedor, momento);
        var hoy = DateOnly.FromDateTime(momento.UtcDateTime);

        // La línea que no cambia conserva el precio sugerido guardado: recalcularlo con los márgenes de hoy lo movería sin
        // que nadie haya tocado la línea.
        var sinUsar = itemsExistentes.Where(i => i.IdArticulo is not null && i.PrecioSugerido is not null).ToList();
        if (comprobante.DiscriminaIva == discriminaIva && comprobante.PreciosIncluyenIva == solicitud.PreciosIncluyenIva)
        {
            foreach (var item in itemsNuevos.Where(i => i.IdArticulo is not null))
            {
                var previa = sinUsar.FirstOrDefault(e =>
                    e.IdArticulo == item.IdArticulo && e.CostoUnitario == item.CostoUnitario && e.Cantidad == item.Cantidad
                    && e.Descuento == item.Descuento && e.IdAlicuotaIva == item.IdAlicuotaIva
                    && e.PorcentajeIva == item.PorcentajeIva && e.ImpuestoInterno == item.ImpuestoInterno
                    && e.Total == item.Total && e.ActualizaCosto == item.ActualizaCosto);

                if (previa is not null)
                {
                    item.PrecioSugerido = previa.PrecioSugerido;
                    sinUsar.Remove(previa);
                }
            }
        }

        var idsArticulo = itemsNuevos.Where(i => i.IdArticulo is not null).Select(i => i.IdArticulo!.Value).Distinct().ToList();
        var controlaLotePorArticulo = await db.Articulos
            .Where(a => idsArticulo.Contains(a.Id))
            .ToDictionaryAsync(a => a.Id, a => a.ControlaLote, ct);
        var lotesHabilitado = await ResolverLotesHabilitadoAsync(idEmpresaNueva, solicitud.IdPuntoVenta, ct);

        var lotesExistentes = itemsExistentes
            .Where(i => i.IdArticulo is not null)
            .Select(i => new LineaDeLoteDeCompra(i.IdArticulo!.Value, i.CodigoLote, i.FechaVencimiento, i.IdLote))
            .ToList();

        LineaDeLoteDeCompra? LoteQueSeConserva(int? idArticulo, string? codigoLote, DateOnly? fechaVencimiento)
        {
            if (idArticulo is not { } articulo)
            {
                return null;
            }

            var coincidente = CalculadorDeEdicionDeCompra.LineaSinCambioDeLote(lotesExistentes, articulo, codigoLote, fechaVencimiento);
            var efectivo = ReglaDeLotes.ControlEfectivo(controlaLotePorArticulo.GetValueOrDefault(articulo), lotesHabilitado);

            // Una línea que se guardó sin lote y ahora lo controla es una línea nueva para el lote.
            return coincidente is not null && (coincidente.IdLote is not null || !efectivo) ? coincidente : null;
        }

        // La excepción cubre solo lo YA recibido: si la suma de la clave (artículo, código, vencimiento) crece respecto de
        // la guardada, el vencimiento se verifica como en la confirmación.
        static (int, string?, DateOnly?) ClaveDeLote(int idArticulo, string? codigoLote, DateOnly? vencimiento) =>
            (idArticulo, string.IsNullOrWhiteSpace(codigoLote) ? null : codigoLote.Trim(), vencimiento);

        var recibidoPorLote = itemsExistentes
            .Where(i => i.IdArticulo is not null)
            .GroupBy(i => ClaveDeLote(i.IdArticulo!.Value, i.CodigoLote, i.FechaVencimiento))
            .ToDictionary(g => g.Key, g => g.Sum(i => i.Cantidad));
        var pedidoPorLote = itemsNuevos
            .Where(i => i.IdArticulo is not null)
            .GroupBy(i => ClaveDeLote(i.IdArticulo!.Value, i.CodigoLote, i.FechaVencimiento))
            .ToDictionary(g => g.Key, g => g.Sum(i => i.Cantidad));

        ValidarVencimientosDeRecepcion(
            solicitud.Items, hoy,
            linea => linea.IdArticulo is { } articulo
                && LoteQueSeConserva(articulo, linea.CodigoLote, linea.FechaVencimiento) is not null
                && pedidoPorLote.GetValueOrDefault(ClaveDeLote(articulo, linea.CodigoLote, linea.FechaVencimiento))
                    <= recibidoPorLote.GetValueOrDefault(ClaveDeLote(articulo, linea.CodigoLote, linea.FechaVencimiento)));

        var itemsPorResolver = new List<ItemComprobanteCompra>();
        foreach (var item in itemsNuevos.Where(i => i.IdArticulo is not null))
        {
            if (LoteQueSeConserva(item.IdArticulo, item.CodigoLote, item.FechaVencimiento) is { } conservado)
            {
                item.IdLote = conservado.IdLote;
            }
            else if (ReglaDeLotes.ControlEfectivo(controlaLotePorArticulo.GetValueOrDefault(item.IdArticulo!.Value), lotesHabilitado))
            {
                itemsPorResolver.Add(item);
            }
        }

        foreach (var item in itemsPorResolver
                     .OrderBy(i => i.IdArticulo)
                     .ThenBy(i => ServicioDeLotes.CodigoResuelto(i.CodigoLote, i.FechaVencimiento), StringComparer.Ordinal))
        {
            if (item.FechaVencimiento is null)
            {
                throw new ErrorDominio(
                    "lote_requerido",
                    $"El artículo {item.IdArticulo} controla lote; la línea necesita codigo_lote/fecha_vencimiento para guardarse.",
                    400);
            }

            if (ReglaDeLotes.EstaVencido(item.FechaVencimiento, hoy))
            {
                throw new ErrorDominio(
                    "lote_vencido_en_recepcion",
                    $"La fecha de vencimiento del artículo {item.IdArticulo} ya pasó; una recepción no puede " +
                    "ingresar mercadería vencida.",
                    409);
            }

            item.IdLote = await ServicioDeLotes.ResolverOCrearAsync(
                conexion, transaccionCruda, idTenant, item.IdArticulo!.Value, item.CodigoLote, item.FechaVencimiento.Value,
                momento, ct);
        }

        // 5. Stock por diferencia contra el libro de la compra (confirmación y ediciones previas), un movimiento por clave
        // que cambia.
        // El libro conserva la sucursal de cada movimiento, o la que la absorbió si se fusionó; lo nuevo va a la sucursal
        // actual de la caja de recepción. Todas las cajas se bloquean antes que cualquier sucursal (orden de
        // EscritorDeStock.ResolverSucursalesDeReversaAsync), la de recepción incluida.
        var libroGuardado = await db.MovimientosStock
            .Where(m => m.IdComprobanteCompra == id && m.Motivo == MotivoStock.Compra)
            .Select(m => new { m.IdArticulo, m.IdSucursal, m.IdPuntoVenta, m.IdLote, m.Cantidad })
            .ToListAsync(ct);
        var sucursalDeReversa = await EscritorDeStock.ResolverSucursalesDeReversaAsync(
            conexion, transaccionCruda, libroGuardado.Select(m => m.IdPuntoVenta).Append(solicitud.IdPuntoVenta),
            libroGuardado.Select(m => m.IdSucursal), ct);
        var libroDeStock = libroGuardado
            .Select(m => new CantidadDeStockDeCompra(
                m.IdArticulo, sucursalDeReversa[m.IdSucursal], m.IdPuntoVenta, m.IdLote, m.Cantidad))
            .ToList();
        var idSucursalNueva = await EscritorDeStock.ResolverSucursalAsync(conexion, transaccionCruda, solicitud.IdPuntoVenta, ct);
        var stockNuevo = itemsNuevos
            .Where(i => i.IdArticulo is not null)
            .Select(i => new CantidadDeStockDeCompra(
                i.IdArticulo!.Value, idSucursalNueva, solicitud.IdPuntoVenta, i.IdLote, i.Cantidad))
            .ToList();
        var diferenciasDeStock = CalculadorDeEdicionDeCompra.DiferenciasDeStock(libroDeStock, stockNuevo);

        await AsentarDiferenciasDeStockAsync(conexion, transaccionCruda, idTenant, id, idEmpleado, momento, diferenciasDeStock, ct);

        // 5.b Costos (E6-S4): después de stock y antes de los códigos y los proveedores, igual que la confirmación. Solo se
        // reescribe el costo de los artículos cuya línea lo actualiza y cuyo costo efectivo cambió respecto de la versión
        // anterior de la compra, y nunca el de uno que una compra confirmada más nueva ya domina. El encabezado todavía
        // tiene el discrimina_iva y precios_incluyen_iva anteriores: se leen antes del paso 6.
        var costos = await ActualizarCostosDeLaEdicionAsync(
            conexion, transaccionCruda, idTenant, id, comprobante.FechaRecepcion ?? momento,
            LineasDeCosto(itemsExistentes, comprobante.DiscriminaIva, comprobante.PreciosIncluyenIva),
            LineasDeCosto(itemsNuevos, discriminaIva, solicitud.PreciosIncluyenIva), momento, ct);

        // 6. Encabezado, líneas, alícuotas y percepciones: replace-set por EF (RemoveRange + AddRange, el mismo del
        // borrador). El SaveChanges traduce la unicidad del número del proveedor (23505 → compra_duplicada). La fecha de
        // recepción, el empleado y el origen quedan como estaban.
        db.ItemsComprobanteCompra.RemoveRange(itemsExistentes);
        db.AlicuotasComprobanteCompra.RemoveRange(
            await db.AlicuotasComprobanteCompra.Where(a => a.IdComprobanteCompra == id).ToListAsync(ct));
        db.PercepcionesComprobanteCompra.RemoveRange(
            await db.PercepcionesComprobanteCompra.Where(x => x.IdComprobanteCompra == id).ToListAsync(ct));

        comprobante.IdProveedor = solicitud.IdProveedor;
        comprobante.IdTipoComprobante = solicitud.IdTipoComprobante;
        comprobante.NumeroExterno = numeroExterno;
        comprobante.FechaComprobante = solicitud.FechaComprobante;
        comprobante.IdPuntoVenta = solicitud.IdPuntoVenta;
        comprobante.Observaciones = NormalizarOpcional(solicitud.Observaciones);
        comprobante.IdOrdenCompra = solicitud.IdOrdenCompra;
        comprobante.Subtotal = calculada.Subtotal;
        comprobante.DescuentoTotal = calculada.DescuentoTotal;
        comprobante.ImpuestosInternos = calculada.ImpuestosInternos;
        comprobante.DiscriminaIva = discriminaIva;
        comprobante.PreciosIncluyenIva = solicitud.PreciosIncluyenIva;
        comprobante.IvaTotal = calculada.IvaTotal;
        comprobante.Total = calculada.Total;
        comprobante.UpdatedAt = momento;

        db.ItemsComprobanteCompra.AddRange(itemsNuevos);
        db.AlicuotasComprobanteCompra.AddRange(MaterializarAlicuotas(id, idTenant, calculada, momento));
        db.PercepcionesComprobanteCompra.AddRange(MaterializarPercepciones(id, idTenant, calculada, momento));
        await db.SaveChangesAsync(ct);

        // 6.b Códigos de proveedor (E6-S4): los pares nuevos o cambiados, o todos si cambia el proveedor. Idempotente, y
        // nunca quita un código. Va después del SaveChanges: las líneas ya insertadas toman FOR KEY SHARE sobre cada
        // artículo, que es lo que impide que una baja concurrente deje un código huérfano (mismo argumento que la
        // confirmación con los movimientos de stock).
        foreach (var candidata in CalculadorDeEdicionDeCompra.CodigosDeProveedorAAsociar(
                     itemsExistentes.Select(i => new LineaConCodigoDeProveedor(i.Orden, i.IdArticulo, i.CodigoProveedor)),
                     itemsNuevos.Select(i => new LineaConCodigoDeProveedor(i.Orden, i.IdArticulo, i.CodigoProveedor)),
                     cambiaProveedor))
        {
            await AsociarCodigoDeProveedorSiEstaLibreAsync(
                conexion, transaccionCruda, idTenant, candidata.IdArticulo, solicitud.IdProveedor, candidata.Codigo,
                momento, ct);
        }

        // 7. Órdenes de compra: re-proyección con las líneas ya escritas, en el mismo orden ascendente del paso 2.
        foreach (var idOrden in ordenesAfectadas)
        {
            await EscriturasDeOrdenDeCompra.ProyectarEstadoAsync(conexion, transaccionCruda, idTenant, idOrden, momento, ct);
        }

        // 8. Auditoría, en la misma transacción cruda.
        var nuevo = new ResumenDeCompraEditada(
            solicitud.IdProveedor, solicitud.IdPuntoVenta, numeroExterno, solicitud.IdOrdenCompra, calculada.Total,
            [.. itemsNuevos.Select(i => new LineaAuditadaDeCompra(i.IdArticulo, i.Cantidad, i.Total))]);
        var (valorAnterior, valorNuevo) = PayloadDeAuditoria.EdicionDeCompra(
            anterior, nuevo, diferenciasDeStock, diferenciasDeCuenta, plan.Tratamiento(desvinculados), costos);
        var servicioDeAuditoria = new ServicioDeAuditoria(db, reloj, contexto);
        await servicioDeAuditoria.RegistrarAsync(
            conexion, transaccionCruda,
            new RegistroDeAuditoria(
                idTenant, solicitud.IdPuntoVenta, AccionAuditada.CompraEdicion, id, valorAnterior, valorNuevo),
            ct);

        // 9. proveedores — ÚLTIMO lock de fila for update de esta transacción, ascendentes por id, y sus asientos. La
        // reversa del redondeo de la compra (que va al proveedor que lo recibió, el anterior) comparte el recorrido con los
        // asientos de la diferencia: mismo orden total, y para cada proveedor la reversa se asienta antes que la diferencia.
        // Se decide en el paso 3 (el cupo de las aplicaciones ya la da por hecha) pero se escribe acá porque tocar la fila
        // del proveedor antes de lotes y stock rompería el orden de locks.
        var proveedoresATocar = diferenciasDeCuenta
            .Select(d => d.IdProveedor)
            .Concat(plan.RevierteElRedondeo ? [anterior.IdProveedor] : [])
            .Distinct()
            .Order()
            .ToList();

        foreach (var idProveedor in proveedoresATocar)
        {
            if (plan.RevierteElRedondeo && idProveedor == anterior.IdProveedor)
            {
                var revertido = await RedondeoDeProveedor.RevertirDeLaCompraAsync(
                    db, conexion, transaccionCruda, idTenant, idProveedor, id, anterior.IdPuntoVenta, idEmpleado, momento,
                    "edición de la compra", ct);

                if (revertido != 0m)
                {
                    await servicioDeAuditoria.RegistrarAsync(
                        conexion, transaccionCruda,
                        new RegistroDeAuditoria(
                            idTenant, anterior.IdPuntoVenta, AccionAuditada.CompraRedondeo, id,
                            new Dictionary<string, object?> { ["redondeo"] = revertido },
                            new Dictionary<string, object?> { ["redondeo"] = 0m, ["motivo"] = "edición de la compra" }),
                        ct);
                }
            }

            foreach (var diferencia in diferenciasDeCuenta.Where(d => d.IdProveedor == idProveedor))
            {
                var nuevoSaldo = await EscriturasDeCuentaCorrienteProveedor.ActualizarSaldoProveedorAsync(
                    conexion, transaccionCruda, idTenant, diferencia.IdProveedor, diferencia.Delta, ct);

                await EscriturasDeCuentaCorrienteProveedor.InsertarMovimientoCcProveedorAsync(
                    conexion, transaccionCruda, idTenant, diferencia.IdProveedor, momento, solicitud.IdPuntoVenta, idEmpleado,
                    TipoMovimientoCcProveedor.Compra, id, idPago: null, diferencia.Delta, nuevoSaldo,
                    DetalleDeLaEdicion(id, anterior, solicitud.IdProveedor, calculada.Total, diferencia.IdProveedor), ct);
            }
        }

        await transaccion.CommitAsync(ct);

        return await ObtenerAsync(id, ct);
    }

    private static string DetalleDeLaEdicion(
        int id, ResumenDeCompraEditada anterior, int idProveedorNuevo, decimal totalNuevo, int idProveedorDelAsiento)
    {
        if (anterior.IdProveedor == idProveedorNuevo)
        {
            return string.Create(
                CultureInfo.InvariantCulture, $"Edición de la compra #{id}: ${anterior.Total:0.00} → ${totalNuevo:0.00}");
        }

        return idProveedorDelAsiento == idProveedorNuevo
            ? $"Edición de la compra #{id}: la compra pasó a este proveedor"
            : $"Edición de la compra #{id}: la compra pasó a otro proveedor";
    }

    /// <summary>Lo que la edición hace con los pagos de la compra, decidido bajo el lock del encabezado.
    /// <paramref name="Recortes"/> son las aplicaciones a achicar o dar de baja; con <paramref name="LiberaTodo"/> (cambia el
    /// proveedor o el punto de venta) son TODAS las vivas y se liberan en una sentencia.</summary>
    private sealed record PlanDePagosDeLaEdicion(
        IReadOnlyList<AplicacionRecortada> Recortes, IReadOnlyDictionary<int, int> PagoDeLaAplicacion, bool LiberaTodo,
        bool RevierteElRedondeo, decimal RedondeoVigente, IReadOnlyList<int> PagosADesvincular)
    {
        /// <summary>Pagos cuyas aplicaciones cambian o cuyo vínculo se corta, ascendentes y sin repetir: los que se bloquean.</summary>
        public IReadOnlyList<int> IdsDePagosAfectados =>
        [
            .. Recortes.Select(r => PagoDeLaAplicacion[r.Id]).Concat(PagosADesvincular).Distinct().Order()
        ];

        public TratamientoDePagosDeLaEdicion Tratamiento(IReadOnlyList<PagoDesvinculadoPorLaEdicion> desvinculados) => new(
            [.. Recortes.Select(r => new AplicacionTratadaPorLaEdicion(r.Id, PagoDeLaAplicacion[r.Id], r.ImporteAnterior, r.ImporteNuevo))],
            RevierteElRedondeo ? Math.Abs(RedondeoVigente) : 0m,
            desvinculados);
    }

    /// <summary>E6-S3. Con la compra bloqueada: las aplicaciones vivas, el redondeo vigente y los pagos vinculados por
    /// <c>pagos.id_comprobante_compra</c> se leen UNA vez (los pagos solo cambian aplicaciones tomando antes el lock de la
    /// compra, así que la lectura es estable hasta el commit). Reglas, sin 409 de por medio:
    /// <list type="bullet">
    /// <item>Cambia el proveedor o el punto de venta (con él, la empresa): se liberan TODAS las aplicaciones vivas y se
    /// cortan los vínculos de los pagos. El pago conserva su proveedor y todo su asiento de cuenta (el crédito sigue con el
    /// proveedor anterior); lo liberado vuelve a ser disponible de cada pago y nada se reaplica.</item>
    /// <item>El total baja por debajo de lo aplicado: se recortan las aplicaciones, la más nueva primero
    /// (<see cref="RecorteDeAplicaciones"/>, el mismo algoritmo que la edición de un pago) hasta que la suma sea el total
    /// nuevo.</item>
    /// <item>El redondeo del lado de la compra se revierte si cambia el total, el proveedor o el punto de venta
    /// (<see cref="CalculadorDeEdicionDeCompra.RevierteElRedondeo"/>): el cupo se calcula con lo perdonado ya en cero, pero
    /// el asiento de la reversa se escribe con el resto de los proveedores, al final.</item>
    /// </list></summary>
    private async Task<PlanDePagosDeLaEdicion> PlanearElTratamientoDePagosAsync(
        int id, decimal totalAnterior, decimal totalNuevo, bool cambiaLaPertenencia, CancellationToken ct)
    {
        var vivas = await db.AplicacionesPago
            .AsNoTracking()
            .Where(a => a.IdComprobanteCompra == id)
            .Select(a => new { a.Id, a.IdPago, a.Fecha, a.Importe })
            .ToListAsync(ct);

        var redondeoVigente = await RedondeoDeProveedor.NetoDeLaCompraAsync(db, id, ct);
        var vinculados = cambiaLaPertenencia
            ? await db.Pagos.AsNoTracking().Where(p => p.IdComprobanteCompra == id).Select(p => p.Id).ToListAsync(ct)
            : [];

        var tope = cambiaLaPertenencia ? 0m : totalNuevo;
        var recortes = RecorteDeAplicaciones.Planificar(vivas.Select(a => new AplicacionARecortar(a.Id, a.Fecha, a.Importe)), tope);

        return new PlanDePagosDeLaEdicion(
            recortes, vivas.ToDictionary(a => a.Id, a => a.IdPago), cambiaLaPertenencia && vivas.Count > 0,
            CalculadorDeEdicionDeCompra.RevierteElRedondeo(redondeoVigente, totalAnterior, totalNuevo, cambiaLaPertenencia),
            redondeoVigente, [.. vinculados.Order()]);
    }

    /// <summary>Lock <c>FOR UPDATE</c> de cada pago afectado, ascendentes por id. Sin filtro de baja: un pago con aplicación
    /// viva no puede estar dado de baja (lo exige el trigger) y uno que ya no existe simplemente no toma nada.</summary>
    private static async Task BloquearPagosDeLaEdicionAsync(
        DbConnection conexion, DbTransaction? transaccion, int idTenant, IReadOnlyList<int> idsDePagos, CancellationToken ct)
    {
        foreach (var idPago in idsDePagos)
        {
            await using var comando = conexion.CreateCommand();
            comando.Transaction = transaccion;
            comando.CommandText = "SELECT 1 FROM pagos WHERE id_pago = $1 AND id_tenant = $2 FOR UPDATE";
            ParametrosDeComando.Agregar(comando, idPago);
            ParametrosDeComando.Agregar(comando, idTenant);
            await comando.ExecuteScalarAsync(ct);
        }
    }

    /// <summary>Escribe el plan con los pagos ya bloqueados: libera todas las aplicaciones (una sentencia) o da de baja y
    /// achica las del recorte, y corta los vínculos <c>pagos.id_comprobante_compra</c> de los pagos que dejan de ser de esta
    /// compra. El asiento <c>pago</c> de cuenta corriente de cada pago NO se toca (libro de solo agregado): conserva su
    /// referencia histórica a la compra (tras un cambio de proveedor queda en el libro del proveedor anterior) y su crédito
    /// con él. Un pago sin punto de venta propio tomaba el de la compra: al desvincularlo hereda el que la compra tenía
    /// (<paramref name="idPuntoVentaAnterior"/>) si ese punto de venta está vivo y es de la empresa del pago; si no, toma el
    /// vivo de menor id de su empresa, para que el pago siga siendo editable. Solo una empresa sin ningún punto de venta
    /// vivo lo deja en <c>NULL</c> (sus ajustes de cuenta usan entonces el camino de
    /// <c>ServicioDePagos.EscribirAjustesDeProveedorAsync</c>). Devuelve cada pago desvinculado y el punto de venta que
    /// heredó (<c>null</c> si no heredó ninguno), ascendentes por id, para el rastro.</summary>
    private static async Task<IReadOnlyList<PagoDesvinculadoPorLaEdicion>> AplicarElTratamientoDePagosAsync(
        DbConnection conexion, DbTransaction? transaccion, int id, int idTenant, PlanDePagosDeLaEdicion plan,
        int idPuntoVentaAnterior, DateTimeOffset momento, CancellationToken ct)
    {
        if (plan.LiberaTodo)
        {
            await LiberarAplicacionesDeLaCompraAsync(conexion, transaccion, id, idTenant, momento, ct);
        }
        else
        {
            foreach (var recorte in plan.Recortes)
            {
                await using var comando = conexion.CreateCommand();
                comando.Transaction = transaccion;
                comando.CommandText = recorte.Baja
                    ? "UPDATE aplicaciones_pago SET deleted_at = $1, updated_at = $1 " +
                      "WHERE id_aplicacion_pago = $2 AND id_tenant = $3 AND deleted_at IS NULL"
                    : "UPDATE aplicaciones_pago SET importe = $4, updated_at = $1 " +
                      "WHERE id_aplicacion_pago = $2 AND id_tenant = $3 AND deleted_at IS NULL";
                ParametrosDeComando.Agregar(comando, momento);
                ParametrosDeComando.Agregar(comando, recorte.Id);
                ParametrosDeComando.Agregar(comando, idTenant);
                if (!recorte.Baja)
                {
                    ParametrosDeComando.Agregar(comando, recorte.ImporteNuevo);
                }

                await comando.ExecuteNonQueryAsync(ct);
            }
        }

        if (plan.PagosADesvincular.Count == 0)
        {
            return [];
        }

        var desvinculados = new List<PagoDesvinculadoPorLaEdicion>();

        await using var desvincular = conexion.CreateCommand();
        desvincular.Transaction = transaccion;
        desvincular.CommandText =
            "WITH antes AS (" +
            "  SELECT id_pago, id_punto_venta AS id_punto_venta_antes FROM pagos " +
            "  WHERE id_comprobante_compra = $2 AND id_tenant = $3 AND deleted_at IS NULL) " +
            "UPDATE pagos p SET id_comprobante_compra = NULL, updated_at = $1, " +
            "id_punto_venta = COALESCE(p.id_punto_venta, (SELECT pv.id_punto_venta FROM puntos_venta pv " +
            "  WHERE pv.id_punto_venta = $4 AND pv.id_tenant = $3 AND pv.id_empresa = p.id_empresa AND pv.deleted_at IS NULL), " +
            "  (SELECT MIN(pv.id_punto_venta) FROM puntos_venta pv " +
            "  WHERE pv.id_tenant = $3 AND pv.id_empresa = p.id_empresa AND pv.deleted_at IS NULL)) " +
            "FROM antes a WHERE p.id_pago = a.id_pago " +
            "RETURNING p.id_pago, a.id_punto_venta_antes, p.id_punto_venta";
        ParametrosDeComando.Agregar(desvincular, momento);
        ParametrosDeComando.Agregar(desvincular, id);
        ParametrosDeComando.Agregar(desvincular, idTenant);
        ParametrosDeComando.Agregar(desvincular, idPuntoVentaAnterior);

        await using (var lector = await desvincular.ExecuteReaderAsync(ct))
        {
            while (await lector.ReadAsync(ct))
            {
                var propio = await lector.IsDBNullAsync(1, ct) ? (int?)null : lector.GetInt32(1);
                var actual = await lector.IsDBNullAsync(2, ct) ? (int?)null : lector.GetInt32(2);
                desvinculados.Add(new PagoDesvinculadoPorLaEdicion(lector.GetInt32(0), propio is null ? actual : null));
            }
        }

        return [.. desvinculados.OrderBy(d => d.IdPago)];
    }

    /// <summary>Un movimiento <c>compra</c> con signo por cada clave que cambia (con su caja), el upsert del agregado de
    /// stock por (sucursal, artículo) y el de cada lote por (sucursal, artículo, lote). Las diferencias vienen en el orden
    /// de locks de <see cref="EscritorDeStock"/>; el agregado y cada lote se escriben una vez con la suma de sus claves,
    /// así que un lote que baja y otro que sube, o dos cajas de la misma sucursal, no producen un negativo transitorio. Un
    /// negativo solo se rechaza donde la edición RESTA: <c>compra_edicion_stock_negativo</c>.</summary>
    private static async Task AsentarDiferenciasDeStockAsync(
        DbConnection conexion, DbTransaction? transaccion, int idTenant, int idCompra, int idEmpleado,
        DateTimeOffset momento, IReadOnlyList<DiferenciaDeStockDeCompra> diferencias, CancellationToken ct)
    {
        var observaciones = $"Edición de la compra #{idCompra}";

        foreach (var grupo in diferencias.GroupBy(d => (d.IdSucursal, d.IdArticulo)))
        {
            foreach (var diferencia in grupo)
            {
                await EscritorDeStock.InsertarMovimientoAsync(
                    conexion, transaccion,
                    new AsientoDeStock(
                        idTenant, diferencia.IdSucursal, diferencia.IdPuntoVenta, diferencia.IdArticulo, diferencia.Delta,
                        MotivoStock.Compra, idEmpleado, momento)
                    {
                        IdLote = diferencia.IdLote,
                        Observaciones = observaciones,
                        IdComprobanteCompra = idCompra
                    },
                    ct);
            }

            var netoDelGrupo = grupo.Sum(d => d.Delta);
            if (netoDelGrupo == 0m && grupo.Any(d => d.IdLote is not null))
            {
                // Un lote que sube y otro que baja netean cero en el agregado, pero los lotes se bloquean igual: la
                // fila agregada se toma antes, como manda el orden de locks de EscritorDeStock.
                await EscritorDeStock.BloquearYCrearSiFaltaAsync(
                    conexion, transaccion, idTenant, grupo.Key.IdSucursal, grupo.Key.IdArticulo, ct);
            }
            else if (netoDelGrupo != 0m)
            {
                var agregado = await EscritorDeStock.SumarAStockAsync(
                    conexion, transaccion, idTenant, grupo.Key.IdSucursal, grupo.Key.IdArticulo, netoDelGrupo, ct);

                if (netoDelGrupo < 0m && agregado < 0m)
                {
                    throw new ErrorDominio(
                        "compra_edicion_stock_negativo",
                        $"El artículo {grupo.Key.IdArticulo} quedaría con stock negativo en la sucursal " +
                        $"{grupo.Key.IdSucursal} al editar esta compra.",
                        409);
                }
            }

            foreach (var lote in grupo.Where(d => d.IdLote is not null).GroupBy(d => d.IdLote!.Value))
            {
                var netoDelLote = lote.Sum(d => d.Delta);
                if (netoDelLote == 0m)
                {
                    continue;
                }

                var delLote = await EscritorDeStock.SumarAStockDeLoteAsync(
                    conexion, transaccion, idTenant, grupo.Key.IdSucursal, grupo.Key.IdArticulo, lote.Key, netoDelLote, ct);

                if (netoDelLote < 0m && delLote < 0m)
                {
                    throw new ErrorDominio(
                        "compra_edicion_stock_negativo",
                        $"El lote {lote.Key} del artículo {grupo.Key.IdArticulo} quedaría con stock negativo al " +
                        "editar esta compra.",
                        409);
                }
            }
        }
    }

    private static async Task<bool> BloquearConfirmadaAsync(
        DbConnection conexion, DbTransaction? transaccion, int id, int idTenant, CancellationToken ct)
    {
        await using var comando = conexion.CreateCommand();
        comando.Transaction = transaccion;
        comando.CommandText =
            "SELECT 1 FROM comprobantes_compra " +
            "WHERE id_comprobante_compra = $1 AND id_tenant = $2 AND estado = 'confirmada'::estado_compra " +
            "FOR UPDATE";

        ParametrosDeComando.Agregar(comando, id);
        ParametrosDeComando.Agregar(comando, idTenant);

        return await comando.ExecuteScalarAsync(ct) is not null;
    }

    private static List<LineaDeCostoDeCompra> LineasDeCosto(
        IEnumerable<ItemComprobanteCompra> items, bool discriminaIva, bool preciosIncluyenIva) =>
    [
        .. items
            .Where(i => i.IdArticulo is not null)
            .Select(i => new LineaDeCostoDeCompra(
                i.Orden, i.IdArticulo, i.ActualizaCosto, i.CostoUnitario,
                CalculadorDeCompra.CalcularCostoEfectivoDesdeItem(
                    i.Total, i.Cantidad, i.PorcentajeIva, discriminaIva, preciosIncluyenIva, i.ImpuestoInterno)))
    ];

    private static bool ActualizaElCosto(LineaDeCostoDeCompra l) =>
        CalculadorDeCompra.ActualizaElCosto((l.Orden, l.IdArticulo, l.ActualizaCosto, l.CostoUnitario, l.CostoEfectivo));

    /// <summary>E6-S4. Reescribe <c>articulos.costo_nominal</c> con el MISMO camino de la confirmación (lectura de familias
    /// bajo el lock de membresía compartido, <see cref="BloquearArticulosConCostoAsync"/> con las filas ascendentes y un solo
    /// <see cref="ActualizarCostosNominalesAsync"/>), pero solo para lo que la edición cambia
    /// (<see cref="CalculadorDeEdicionDeCompra.CostosQueCambian"/>): sin cambio de costo efectivo no hay ninguna ida a la
    /// base. La compra más nueva se busca DESPUÉS de tomar los locks de artículo, con una lectura propia: una confirmación
    /// concurrente de otra compra que ya comiteó se ve, y una que no, espera a este lock y escribe después.</summary>
    private async Task<CostosDeLaEdicion> ActualizarCostosDeLaEdicionAsync(
        DbConnection conexion, DbTransaction? transaccion, int idTenant, int idCompra, DateTimeOffset recepcion,
        IReadOnlyList<LineaDeCostoDeCompra> anteriores, IReadOnlyList<LineaDeCostoDeCompra> nuevas, DateTimeOffset momento,
        CancellationToken ct)
    {
        var sinCambios = new CostosDeLaEdicion([], []);
        var idsConCosto = anteriores.Concat(nuevas).Where(ActualizaElCosto).Select(l => l.IdArticulo!.Value).Distinct().ToList();
        if (idsConCosto.Count == 0)
        {
            return sinCambios;
        }

        var familias = await LeerFamiliasDeLosArticulosAsync(conexion, transaccion, idTenant, idsConCosto, ct);
        var aCambiar = CalculadorDeEdicionDeCompra.CostosQueCambian(anteriores, nuevas, familias);
        if (aCambiar.Count == 0)
        {
            return sinCambios;
        }

        var bloqueados = await BloquearArticulosConCostoAsync(conexion, transaccion, idTenant, aCambiar, ct);
        if (bloqueados.Count == 0)
        {
            return sinCambios;
        }

        var idsBloqueados = bloqueados.Select(a => a.IdArticulo).ToList();
        var posteriores = await db.ItemsComprobanteCompra
            .AsNoTracking()
            .Where(i => i.ActualizaCosto && i.IdArticulo != null && idsBloqueados.Contains(i.IdArticulo.Value))
            .Join(
                db.ComprobantesCompra.AsNoTracking().Where(c =>
                    c.Estado == EstadoCompra.Confirmada && c.Id != idCompra
                    && (c.FechaRecepcion > recepcion || (c.FechaRecepcion == recepcion && c.Id > idCompra))),
                i => i.IdComprobanteCompra, c => c.Id,
                (i, c) => new
                {
                    i.Orden, i.IdArticulo, i.ActualizaCosto, i.CostoUnitario, i.Total, i.Cantidad, i.PorcentajeIva,
                    i.ImpuestoInterno, c.DiscriminaIva, c.PreciosIncluyenIva
                })
            .ToListAsync(ct);

        var conLineaPosterior = posteriores
            .Where(p => ActualizaElCosto(new LineaDeCostoDeCompra(
                p.Orden, p.IdArticulo, p.ActualizaCosto, p.CostoUnitario,
                CalculadorDeCompra.CalcularCostoEfectivoDesdeItem(
                    p.Total, p.Cantidad, p.PorcentajeIva, p.DiscriminaIva, p.PreciosIncluyenIva, p.ImpuestoInterno))))
            .Select(p => p.IdArticulo!.Value)
            .ToHashSet();

        var familiasDeLasFilas = conLineaPosterior.Count == 0
            ? new Dictionary<int, int>()
            : await LeerFamiliasDeLosArticulosAsync(
                conexion, transaccion, idTenant, [.. idsBloqueados.Concat(conLineaPosterior).Distinct()], ct);
        var (aplicables, omitidos) = CalculadorDeEdicionDeCompra.SinLosPisadosPorUnaCompraMasNueva(
            bloqueados, conLineaPosterior, familiasDeLasFilas);

        var anterioresDelCosto = await LeerCostosNominalesAsync(conexion, transaccion, idTenant, [.. aplicables.Select(a => a.IdArticulo)], ct);

        await ActualizarCostosNominalesAsync(conexion, transaccion, idTenant, aplicables, momento, ct);

        return new CostosDeLaEdicion(
            [.. aplicables.Select(a => new CostoActualizadoPorLaEdicion(
                a.IdArticulo, a.Costo, anterioresDelCosto.TryGetValue(a.IdArticulo, out var previo) ? previo : null))],
            omitidos);
    }

    /// <summary>Costo nominal vigente de las filas de artículo ya bloqueadas, antes de que la edición lo reescriba.</summary>
    private static async Task<Dictionary<int, decimal?>> LeerCostosNominalesAsync(
        DbConnection conexion, DbTransaction? transaccion, int idTenant, int[] idsArticulo, CancellationToken ct)
    {
        var costos = new Dictionary<int, decimal?>();
        if (idsArticulo.Length == 0)
        {
            return costos;
        }

        await using var comando = conexion.CreateCommand();
        comando.Transaction = transaccion;
        comando.CommandText = "SELECT id_articulo, costo_nominal FROM articulos WHERE id_tenant = $1 AND id_articulo = ANY($2)";
        ParametrosDeComando.Agregar(comando, idTenant);
        ParametrosDeComando.Agregar(comando, idsArticulo);

        await using var lector = await comando.ExecuteReaderAsync(ct);
        while (await lector.ReadAsync(ct))
        {
            costos[lector.GetInt32(0)] = await lector.IsDBNullAsync(1, ct) ? null : lector.GetDecimal(1);
        }

        return costos;
    }
}
