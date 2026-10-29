using System.Data.Common;
using System.Globalization;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Storage;
using Ways.Application.Abstracciones;
using Ways.Application.Auditoria;
using Ways.Application.CuentaCorriente;
using Ways.Application.Familias;
using Ways.Application.Stock;
using Ways.Domain.Auditoria;
using Ways.Domain.Common;
using Ways.Domain.Compras;
using Ways.Domain.CuentaCorriente;
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
/// lotes (ascendente por artículo y código RESUELTO, ordinal: <see cref="ServicioDeLotes.CodigoResuelto"/>, igual que la confirmación) → stock (ascendente por artículo y punto de venta, agregado antes que lotes) →
/// escritura de encabezado, líneas, alícuotas y percepciones por EF → re-proyección de las órdenes → proveedores,
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

        // 3. Pagos de la compra. TEMPORAL (E6-S3 lo reemplaza por el tratamiento de las aplicaciones): mientras tanto, una
        // edición que toca lo que los pagos aplicados o el redondeo perdonado suponen se rechaza.
        await ExigirQueLaEdicionNoToqueLosPagosAsync(
            id, comprobante, solicitud, cambiaProveedor, cambiaPuntoVenta,
            cambiaEmpresa: cambiaPuntoVenta && await EmpresaDelPuntoVentaAsync(comprobante.IdPuntoVenta, ct) != idEmpresaNueva,
            calculada.Total, ct);

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
        var libroDeStock = (await db.MovimientosStock
                .Where(m => m.IdComprobanteCompra == id && m.Motivo == MotivoStock.Compra)
                .Select(m => new { m.IdArticulo, m.IdPuntoVenta, m.IdLote, m.Cantidad })
                .ToListAsync(ct))
            .Select(m => new CantidadDeStockDeCompra(m.IdArticulo, m.IdPuntoVenta, m.IdLote, m.Cantidad))
            .ToList();
        var stockNuevo = itemsNuevos
            .Where(i => i.IdArticulo is not null)
            .Select(i => new CantidadDeStockDeCompra(i.IdArticulo!.Value, solicitud.IdPuntoVenta, i.IdLote, i.Cantidad))
            .ToList();
        var diferenciasDeStock = CalculadorDeEdicionDeCompra.DiferenciasDeStock(libroDeStock, stockNuevo);

        await AsentarDiferenciasDeStockAsync(conexion, transaccionCruda, idTenant, id, idEmpleado, momento, diferenciasDeStock, ct);

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

        // 7. Órdenes de compra: re-proyección con las líneas ya escritas, en el mismo orden ascendente del paso 2.
        foreach (var idOrden in ordenesAfectadas)
        {
            await EscriturasDeOrdenDeCompra.ProyectarEstadoAsync(conexion, transaccionCruda, idTenant, idOrden, momento, ct);
        }

        // 8. Auditoría, en la misma transacción cruda.
        var nuevo = new ResumenDeCompraEditada(
            solicitud.IdProveedor, solicitud.IdPuntoVenta, numeroExterno, solicitud.IdOrdenCompra, calculada.Total,
            [.. itemsNuevos.Select(i => new LineaAuditadaDeCompra(i.IdArticulo, i.Cantidad, i.Total))]);
        var (valorAnterior, valorNuevo) = PayloadDeAuditoria.EdicionDeCompra(anterior, nuevo, diferenciasDeStock, diferenciasDeCuenta);
        await new ServicioDeAuditoria(db, reloj, contexto).RegistrarAsync(
            conexion, transaccionCruda,
            new RegistroDeAuditoria(
                idTenant, solicitud.IdPuntoVenta, AccionAuditada.CompraEdicion, id, valorAnterior, valorNuevo),
            ct);

        // 9. proveedores — ÚLTIMO lock de fila for update de esta transacción (ascendente por id), y su asiento.
        foreach (var diferencia in diferenciasDeCuenta)
        {
            var nuevoSaldo = await EscriturasDeCuentaCorrienteProveedor.ActualizarSaldoProveedorAsync(
                conexion, transaccionCruda, idTenant, diferencia.IdProveedor, diferencia.Delta, ct);

            await EscriturasDeCuentaCorrienteProveedor.InsertarMovimientoCcProveedorAsync(
                conexion, transaccionCruda, idTenant, diferencia.IdProveedor, momento, solicitud.IdPuntoVenta, idEmpleado,
                TipoMovimientoCcProveedor.Compra, id, idPago: null, diferencia.Delta, nuevoSaldo,
                DetalleDeLaEdicion(id, anterior, solicitud.IdProveedor, calculada.Total, diferencia.IdProveedor), ct);
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

    private async Task<int> EmpresaDelPuntoVentaAsync(int idPuntoVenta, CancellationToken ct) =>
        await db.PuntosVenta.Where(pv => pv.Id == idPuntoVenta).Select(pv => pv.IdEmpresa).FirstAsync(ct);

    /// <summary>TEMPORAL — reemplazado por E6-S3. Rechaza con 409 <c>compra_edicion_con_aplicaciones</c> la edición que
    /// <see cref="CalculadorDeEdicionDeCompra.RequiereTratarLosPagos"/> marca. Lectura de las aplicaciones vivas y del
    /// redondeo bajo el lock del encabezado, que serializa a todo escritor de aplicaciones de la compra.</summary>
    private async Task ExigirQueLaEdicionNoToqueLosPagosAsync(
        int id, ComprobanteCompra comprobante, SolicitudDeCompra solicitud, bool cambiaProveedor,
        bool cambiaPuntoVenta, bool cambiaEmpresa, decimal totalNuevo, CancellationToken ct)
    {
        var aplicado = await db.AplicacionesPago
            .Where(a => a.IdComprobanteCompra == id)
            .SumAsync(a => (decimal?)a.Importe, ct) ?? 0m;
        var tieneAplicaciones = await db.AplicacionesPago.AnyAsync(a => a.IdComprobanteCompra == id, ct);
        var perdonado = -(await db.MovimientosCuentaCorrienteProveedor
            .Where(m => m.IdComprobanteCompra == id && m.Tipo == TipoMovimientoCcProveedor.Redondeo)
            .SumAsync(m => (decimal?)m.Importe, ct) ?? 0m);

        // Un pago vivo vinculado por pagos.id_comprobante_compra ata la compra a su proveedor y empresa aunque no tenga
        // aplicación viva.
        if ((cambiaProveedor || cambiaPuntoVenta || cambiaEmpresa)
            && await db.Pagos.AnyAsync(p => p.IdComprobanteCompra == id, ct))
        {
            throw new ErrorDominio(
                "compra_edicion_con_aplicaciones",
                "La compra tiene pagos vinculados: no puede cambiar de proveedor, punto de venta ni empresa. Desvinculá o " +
                "dá de baja los pagos antes de editarla.",
                409);
        }

        if (CalculadorDeEdicionDeCompra.RequiereTratarLosPagos(
                aplicado + perdonado, perdonado, tieneAplicaciones, comprobante.Total, totalNuevo,
                cambiaProveedor, cambiaPuntoVenta, cambiaEmpresa))
        {
            throw new ErrorDominio(
                "compra_edicion_con_aplicaciones",
                "La edición toca lo que los pagos de la compra suponen (el total quedaría por debajo de lo pagado, o cambia " +
                "el proveedor, el punto de venta o la empresa de una compra con pagos aplicados o redondeo). Desaplicá los " +
                "pagos antes de editarla.",
                409);
        }
    }

    /// <summary>Un movimiento <c>compra</c> con signo por cada clave que cambia, el upsert del agregado de stock por
    /// (artículo, punto de venta) y el de cada lote. Las diferencias vienen ascendentes por artículo, punto de venta y
    /// lote; el agregado se escribe una vez por (artículo, punto de venta) con la suma de sus claves, así que un lote que
    /// baja y otro que sube del mismo artículo no producen un negativo transitorio. Un negativo solo se rechaza donde la
    /// edición RESTA: <c>compra_edicion_stock_negativo</c>.</summary>
    private static async Task AsentarDiferenciasDeStockAsync(
        DbConnection conexion, DbTransaction? transaccion, int idTenant, int idCompra, int idEmpleado,
        DateTimeOffset momento, IReadOnlyList<DiferenciaDeStockDeCompra> diferencias, CancellationToken ct)
    {
        var observaciones = $"Edición de la compra #{idCompra}";

        foreach (var grupo in diferencias.GroupBy(d => (d.IdArticulo, d.IdPuntoVenta)))
        {
            foreach (var diferencia in grupo)
            {
                await InsertarMovimientoStockAsync(
                    conexion, transaccion, idTenant, diferencia.IdArticulo, diferencia.IdPuntoVenta, diferencia.Delta,
                    MotivoStock.Compra, idCompra, idEmpleado, momento, diferencia.IdLote, observaciones, ct);
            }

            var netoDelGrupo = grupo.Sum(d => d.Delta);
            if (netoDelGrupo != 0m)
            {
                var agregado = await UpsertStockAsync(
                    conexion, transaccion, idTenant, grupo.Key.IdArticulo, grupo.Key.IdPuntoVenta, netoDelGrupo, ct);

                if (netoDelGrupo < 0m && agregado < 0m)
                {
                    throw new ErrorDominio(
                        "compra_edicion_stock_negativo",
                        $"El artículo {grupo.Key.IdArticulo} quedaría con stock negativo en el punto de venta " +
                        $"{grupo.Key.IdPuntoVenta} al editar esta compra.",
                        409);
                }
            }

            foreach (var diferencia in grupo.Where(d => d.IdLote is not null))
            {
                var delLote = await UpsertStockLoteAsync(
                    conexion, transaccion, idTenant, diferencia.IdArticulo, diferencia.IdPuntoVenta, diferencia.IdLote!.Value,
                    diferencia.Delta, ct);

                if (diferencia.Delta < 0m && delLote < 0m)
                {
                    throw new ErrorDominio(
                        "compra_edicion_stock_negativo",
                        $"El lote {diferencia.IdLote} del artículo {diferencia.IdArticulo} quedaría con stock negativo al " +
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
}
