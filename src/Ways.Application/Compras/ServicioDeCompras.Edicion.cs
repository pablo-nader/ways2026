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

        // 3. Pagos de la compra (E6-S3). Se planea con lecturas posteriores al lock del encabezado, que serializa a todo
        // escritor de aplicaciones de la compra (todos lo toman antes que el pago), y se bloquean los pagos afectados
        // ascendentes por id: después de las órdenes y ANTES de lotes y stock, antes del proveedor, que sigue último.
        var plan = await PlanearElTratamientoDePagosAsync(
            id, comprobante.Total, calculada.Total, cambiaProveedor || cambiaPuntoVenta, ct);

        await BloquearPagosDeLaEdicionAsync(conexion, transaccionCruda, idTenant, plan.IdsDePagosAfectados, ct);
        await AplicarElTratamientoDePagosAsync(conexion, transaccionCruda, id, idTenant, plan, momento, ct);

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
        var (valorAnterior, valorNuevo) = PayloadDeAuditoria.EdicionDeCompra(
            anterior, nuevo, diferenciasDeStock, diferenciasDeCuenta, plan.Tratamiento);
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

        public TratamientoDePagosDeLaEdicion Tratamiento => new(
            [.. Recortes.Select(r => new AplicacionTratadaPorLaEdicion(r.Id, PagoDeLaAplicacion[r.Id], r.ImporteAnterior, r.ImporteNuevo))],
            RevierteElRedondeo ? Math.Abs(RedondeoVigente) : 0m,
            PagosADesvincular);
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
    /// referencia histórica a la compra y su crédito con el proveedor anterior.</summary>
    private static async Task AplicarElTratamientoDePagosAsync(
        DbConnection conexion, DbTransaction? transaccion, int id, int idTenant, PlanDePagosDeLaEdicion plan,
        DateTimeOffset momento, CancellationToken ct)
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

        if (plan.PagosADesvincular.Count > 0)
        {
            await using var desvincular = conexion.CreateCommand();
            desvincular.Transaction = transaccion;
            desvincular.CommandText =
                "UPDATE pagos SET id_comprobante_compra = NULL, updated_at = $1 " +
                "WHERE id_comprobante_compra = $2 AND id_tenant = $3 AND deleted_at IS NULL";
            ParametrosDeComando.Agregar(desvincular, momento);
            ParametrosDeComando.Agregar(desvincular, id);
            ParametrosDeComando.Agregar(desvincular, idTenant);
            await desvincular.ExecuteNonQueryAsync(ct);
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
