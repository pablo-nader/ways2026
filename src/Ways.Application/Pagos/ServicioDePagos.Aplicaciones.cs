using Microsoft.EntityFrameworkCore;
using Ways.Application.Abstracciones;
using Ways.Application.Caja;
using Ways.Application.Compras;
using Ways.Domain.Auditoria;
using Ways.Domain.Common;
using Ways.Domain.Compras;
using Ways.Domain.CuentaCorriente;
using Ways.Domain.Organizacion;
using Ways.Domain.Pagos;

namespace Ways.Application.Pagos;

/// <summary>
/// E3-S3: aplicar lo disponible de un pago ya existente a compras, desaplicar, y la adopción de un pago sin proveedor.
/// Orden total de locks, el mismo de los demás escritores de pagos: compras <c>FOR UPDATE</c> en id ascendente → pago
/// <c>FOR UPDATE</c> → fila del proveedor (solo en la adopción, adentro de <see cref="EscribirPagoAProveedorAsync"/>).
/// Nunca toca la tesorería: la plata del pago ya salió cuando se lo cargó.
/// </summary>
public partial class ServicioDePagos
{
    private const int MaximoDeAplicacionesPorPedido = 20;

    /// <summary>
    /// Aplica el disponible de un pago a una o más compras de un mismo proveedor. Un pago de proveedor se aplica sin
    /// más; un pago sin proveedor (categoría distinta de proveedor, o proveedor sin asignar) lo adopta: pasa a categoría
    /// proveedor con el de las compras, escribe el movimiento <c>pago</c> de la cuenta corriente por su importe completo
    /// con la fecha de ahora (recién ahí entra a esa cuenta) y toma la primera compra pedida como «compra de origen»:
    /// el pago no tenía proveedor, así que cualquier vínculo anterior era a una compra que no le corresponde. Toda
    /// compra tiene que ser de la empresa del pago (<c>400 compra_de_otra_empresa</c>). Una sola aplicación viva por
    /// par pago-compra: aplicar de nuevo la agranda.
    ///
    /// update-404-first: pago y compras inexistentes responden 404 antes de validar el cuerpo. Forma (b) de
    /// ef-retry-safe-writes: <see cref="FabricaDeEstrategiaSinReintento"/>, porque la adopción escribe el movimiento y el
    /// saldo del proveedor sin clave de idempotencia.
    /// </summary>
    public async Task<PagoDeAdministracionListado> AplicarAsync(
        int idPago, SolicitudDeAplicarPago solicitud, CancellationToken ct = default)
    {
        var idTenant = ExigirTenantDeLaSesion();
        var idEmpleado = contexto.UsuarioId;
        var momento = reloj.Ahora;

        await ExigirPagoExistenteAsync(idPago, ct);
        var pedidas = ExigirAplicacionesSolicitadas(solicitud);
        await ExigirComprasExistentesAsync(pedidas.Select(a => a.IdComprobanteCompra), ct);
        ExigirImportesDeAplicacionValidos(pedidas);

        var estrategia = FabricaDeEstrategiaSinReintento.CrearEstrategiaSinReintento(db);
        await estrategia.ExecuteAsync(async () =>
            await EjecutarAplicarAsync(idTenant, idPago, pedidas, idEmpleado, momento, ct));

        return await ObtenerDeAdministracionAsync(idPago, ct);
    }

    private async Task<int> EjecutarAplicarAsync(
        int idTenant, int idPago, IReadOnlyList<AplicacionSolicitada> pedidas, int idEmpleado, DateTimeOffset momento,
        CancellationToken ct)
    {
        await using var transaccion = await db.Database.BeginTransactionAsync(ct);

        // Paso 1: las compras, FOR UPDATE y en id ascendente (nunca dos escritores esperándose en orden inverso). Es la
        // ÚNICA lectura del total, del estado y del proveedor de cada una.
        var compras = new Dictionary<int, CompraBloqueada>();
        foreach (var pedida in pedidas.OrderBy(a => a.IdComprobanteCompra))
        {
            compras[pedida.IdComprobanteCompra] = await BloquearCompraAsync(pedida.IdComprobanteCompra, idTenant, ct);
        }

        var proveedores = compras.Values.Select(c => c.IdProveedor).Distinct().ToList();
        if (proveedores.Count > 1)
        {
            throw new ErrorDominio(
                "compras_de_distintos_proveedores", "Las compras indicadas son de proveedores distintos.", 400);
        }

        var idProveedorDeLasCompras = proveedores[0];

        // Paso 2: el pago, FOR UPDATE (lock-only) y la ÚNICA lectura EF de la fila, después del lock.
        await TomarLockDePagoAsync(idPago, idTenant, ct);
        var pago = await db.Pagos.FirstAsync(p => p.Id == idPago, ct);
        ExigirPagoSinPagoDePersonal(pago.IdMovimientoPersonal);
        if (pago.Categoria == CategoriaPago.Sueldos)
        {
            throw new ErrorDominio("pago_no_aplicable", "Un pago de sueldos no se aplica a compras.", 409);
        }

        foreach (var compra in compras.Values)
        {
            await ExigirCompraDeLaEmpresaAsync(compra, pago.IdEmpresa, ct);
        }

        var adopta = pago.Categoria != CategoriaPago.Proveedor || pago.IdProveedor is null;
        if (pago.IdProveedor is { } idProveedorDelPago && idProveedorDelPago != idProveedorDeLasCompras)
        {
            throw new ErrorDominio(
                "compra_de_otro_proveedor", "La compra indicada es de otro proveedor que el del pago.", 409);
        }

        // Lo aplicado y lo pagado se leen DESPUÉS de los dos locks: nadie más puede cambiarlos hasta el commit.
        var aplicaciones = await db.AplicacionesPago.Where(a => a.IdPago == pago.Id).ToListAsync(ct);
        var redondeoDelPago = (await LectorDeDisponiblePorPago.LeerRedondeoAsync(db, [pago.Id], ct))
            .GetValueOrDefault(pago.Id, 0m);
        var disponible = LectorDeDisponiblePorPago.Disponible(pago.Importe, aplicaciones.Sum(a => a.Importe), redondeoDelPago);
        if (pedidas.Sum(a => a.Importe) > disponible)
        {
            throw new ErrorDominio(
                "aplicacion_excede_disponible", "El importe a aplicar supera lo disponible del pago.", 409);
        }

        var pagado = await LectorDePagadoPorCompra.LeerAsync(db, compras.Keys.ToList(), ct);
        foreach (var pedida in pedidas)
        {
            var compra = compras[pedida.IdComprobanteCompra];
            var saldo = ReglaDePagoDeCompra.SaldoPendiente(
                EstadoCompra.Confirmada, compra.Total, pagado.GetValueOrDefault(pedida.IdComprobanteCompra, 0m));
            if (pedida.Importe > saldo)
            {
                throw new ErrorDominio(
                    "aplicacion_excede_saldo", "El importe a aplicar supera el saldo pendiente de la compra.", 409);
            }
        }

        var valorAnterior = PayloadDeAplicacion(pago, aplicaciones, redondeoDelPago);

        foreach (var pedida in pedidas)
        {
            var existente = aplicaciones.FirstOrDefault(a => a.IdComprobanteCompra == pedida.IdComprobanteCompra);
            if (existente is not null)
            {
                existente.Importe += pedida.Importe;
                existente.UpdatedAt = momento;
                continue;
            }

            var nueva = new AplicacionPago
            {
                IdTenant = idTenant,
                IdPago = pago.Id,
                IdComprobanteCompra = pedida.IdComprobanteCompra,
                Importe = pedida.Importe,
                Origen = OrigenAplicacionPago.Aplicacion,
                IdEmpleado = idEmpleado,
                Fecha = momento,
                CreatedAt = momento,
                UpdatedAt = momento
            };
            db.AplicacionesPago.Add(nueva);
            aplicaciones.Add(nueva);
        }

        if (adopta)
        {
            pago.Categoria = CategoriaPago.Proveedor;
            pago.IdProveedor = idProveedorDeLasCompras;
            pago.IdComprobanteCompra = pedidas[0].IdComprobanteCompra;
        }

        pago.UpdatedAt = momento;
        await db.SaveChangesAsync(ct);

        if (adopta)
        {
            // Paso 3, último lock de fila: el proveedor. El movimiento ledger exige un punto de venta: el del pago manda
            // cuando existe y si no, el de la compra de origen (las compras siempre tienen uno).
            var puntoVenta = pago.IdPuntoVenta ?? compras[pedidas[0].IdComprobanteCompra].IdPuntoVenta;
            await EscribirPagoAProveedorAsync(
                idTenant, idProveedorDeLasCompras, puntoVenta, pago.Importe, pago, idEmpleado, momento, ct);
        }

        auditoria.Registrar(new RegistroDeAuditoria(
            idTenant, pago.IdPuntoVenta, AccionAuditada.PagoAplicacion, pago.Id, valorAnterior,
            PayloadDeAplicacion(pago, aplicaciones, redondeoDelPago)));
        await db.SaveChangesAsync(ct);

        await transaccion.CommitAsync(ct);

        return pago.Id;
    }

    /// <summary>
    /// Libera una aplicación (baja lógica). El lugar que deja vuelve al saldo de la compra y el resto del pago queda
    /// disponible: no se reaplica nada solo, el administrador lo aplica de forma explícita con
    /// <see cref="AplicarAsync"/>. El proveedor adoptado se queda y <c>pagos.id_comprobante_compra</c> no se toca: es la
    /// «compra de origen» del pago y la imputación de su movimiento de cuenta corriente.
    ///
    /// update-404-first: el pago y la aplicación (que tiene que ser de ese pago y estar viva) responden 404 antes de
    /// cualquier otra cosa. Orden de locks: compra <c>FOR UPDATE</c> → pago <c>FOR UPDATE</c> → fila del proveedor (solo si
    /// la compra tenía un redondeo vigente, que se revierte: perdonaba una diferencia que dependía de esta aplicación).
    /// </summary>
    public async Task<PagoDeAdministracionListado> DesaplicarAsync(
        int idPago, int idAplicacion, CancellationToken ct = default)
    {
        var idTenant = ExigirTenantDeLaSesion();
        var idEmpleado = contexto.UsuarioId;
        var momento = reloj.Ahora;

        await ExigirPagoExistenteAsync(idPago, ct);
        var idCompra = await db.AplicacionesPago
            .Where(a => a.Id == idAplicacion && a.IdPago == idPago)
            .Select(a => (int?)a.IdComprobanteCompra)
            .FirstOrDefaultAsync(ct)
            ?? throw ErrorDominio.NoEncontrado($"No existe la aplicación {idAplicacion} del pago {idPago}.");

        var estrategia = FabricaDeEstrategiaSinReintento.CrearEstrategiaSinReintento(db);
        await estrategia.ExecuteAsync(async () =>
            await EjecutarDesaplicarAsync(idTenant, idPago, idAplicacion, idCompra, idEmpleado, momento, ct));

        return await ObtenerDeAdministracionAsync(idPago, ct);
    }

    private async Task<int> EjecutarDesaplicarAsync(
        int idTenant, int idPago, int idAplicacion, int idCompra, int idEmpleado, DateTimeOffset momento,
        CancellationToken ct)
    {
        await using var transaccion = await db.Database.BeginTransactionAsync(ct);

        // Una compra anulada ya liberó sus aplicaciones (la lectura de abajo daría 404): el lock no exige confirmada.
        var compra = await BloquearCompraAsync(idCompra, idTenant, ct, exigirConfirmada: false);
        await TomarLockDePagoAsync(idPago, idTenant, ct);
        var pago = await db.Pagos.FirstAsync(p => p.Id == idPago, ct);

        // Única lectura de la fila, bajo los dos locks: si otro escritor la liberó entre el chequeo y el lock, es 404.
        var aplicacion = await db.AplicacionesPago.FirstOrDefaultAsync(a => a.Id == idAplicacion && a.IdPago == idPago, ct)
            ?? throw ErrorDominio.NoEncontrado($"No existe la aplicación {idAplicacion} del pago {idPago}.");

        var anteriores = await db.AplicacionesPago.Where(a => a.IdPago == idPago).ToListAsync(ct);
        var redondeoDelPago = (await LectorDeDisponiblePorPago.LeerRedondeoAsync(db, [pago.Id], ct))
            .GetValueOrDefault(pago.Id, 0m);
        var valorAnterior = PayloadDeAplicacion(pago, anteriores, redondeoDelPago);

        aplicacion.DeletedAt = momento;
        aplicacion.UpdatedAt = momento;

        pago.UpdatedAt = momento;

        auditoria.Registrar(new RegistroDeAuditoria(
            idTenant, pago.IdPuntoVenta, AccionAuditada.PagoDesaplicacion, pago.Id, valorAnterior,
            PayloadDeAplicacion(pago, anteriores.Where(a => a.Id != idAplicacion).ToList(), redondeoDelPago)));
        await db.SaveChangesAsync(ct);

        await RevertirRedondeoDeLaCompraAsync(
            idTenant, idCompra, compra, idEmpleado, momento, $"se desaplicó el pago #{idPago}", ct);

        await transaccion.CommitAsync(ct);

        return pago.Id;
    }

    private static Dictionary<string, object?> PayloadDeAplicacion(
        Pago pago, IReadOnlyCollection<AplicacionPago> aplicaciones, decimal redondeoAbsorbido = 0m)
    {
        var vivas = aplicaciones.Where(a => a.DeletedAt is null).ToList();
        var aplicado = vivas.Sum(a => a.Importe);
        return new Dictionary<string, object?>
        {
            ["categoria"] = pago.Categoria,
            ["id_proveedor"] = pago.IdProveedor,
            ["id_comprobante_compra"] = pago.IdComprobanteCompra,
            ["importe"] = pago.Importe,
            ["aplicado"] = aplicado,
            ["redondeo"] = redondeoAbsorbido,
            ["disponible"] = LectorDeDisponiblePorPago.Disponible(pago.Importe, aplicado, redondeoAbsorbido),
            ["aplicaciones"] = vivas
                .OrderBy(a => a.IdComprobanteCompra)
                .Select(a => new Dictionary<string, object?>
                {
                    ["id_comprobante_compra"] = a.IdComprobanteCompra,
                    ["importe"] = a.Importe
                })
                .ToList()
        };
    }

    private static IReadOnlyList<AplicacionSolicitada> ExigirAplicacionesSolicitadas(SolicitudDeAplicarPago solicitud)
    {
        var pedidas = solicitud.Aplicaciones;
        if (pedidas is null || pedidas.Count == 0)
        {
            throw new ErrorDominio("aplicacion_sin_compras", "Indicá al menos una compra a la que aplicar.", 400);
        }

        if (pedidas.Count > MaximoDeAplicacionesPorPedido)
        {
            throw new ErrorDominio(
                "aplicacion_demasiadas_compras",
                $"Se pueden aplicar hasta {MaximoDeAplicacionesPorPedido} compras por pedido.", 400);
        }

        if (pedidas.Any(a => a is null))
        {
            throw new ErrorDominio("aplicacion_sin_compras", "Cada aplicación tiene que indicar compra e importe.", 400);
        }

        if (pedidas.Select(a => a.IdComprobanteCompra).Distinct().Count() != pedidas.Count)
        {
            throw new ErrorDominio("aplicacion_compra_repetida", "Una compra no puede figurar dos veces.", 400);
        }

        return pedidas;
    }

    /// <summary>update-404-first: una compra inexistente (de otro tenant o sin visibilidad) es 404 antes de validar importes.</summary>
    private async Task ExigirComprasExistentesAsync(IEnumerable<int> idsCompras, CancellationToken ct)
    {
        foreach (var id in idsCompras.OrderBy(i => i))
        {
            if (!await db.ComprobantesCompra.AnyAsync(c => c.Id == id, ct))
            {
                throw ErrorDominio.NoEncontrado($"No existe la compra {id}.");
            }
        }
    }

    private static void ExigirImportesDeAplicacionValidos(IEnumerable<AplicacionSolicitada> pedidas)
    {
        foreach (var pedida in pedidas)
        {
            if (pedida.Importe <= 0m || decimal.Round(pedida.Importe, 2) != pedida.Importe)
            {
                throw new ErrorDominio(
                    "aplicacion_importe_invalido",
                    "El importe a aplicar tiene que ser positivo y con hasta dos decimales.", 400);
            }
        }
    }

    // ---- pagar una compra con pagos a cuenta y con más de lo que se debe (E3-S4) -------------------------------

    /// <summary>
    /// Paga una compra confirmada combinando crédito que ya existe (<c>Aplicaciones</c>: pagos con disponible) y dinero
    /// nuevo (<c>Lineas</c>), en UNA transacción. Primero se aplica lo existente y después se crea el pago nuevo por el
    /// total de sus líneas: se aplica a la compra solo hasta el saldo que quede y el resto queda disponible en ese pago
    /// (crédito a favor; el movimiento de cuenta corriente es el del pago completo, así que el saldo del proveedor puede
    /// quedar a favor). A diferencia de <see cref="PagarCompraAsync(int, ComandoDePagoDeCompra, CancellationToken)"/>, esta
    /// ruta nunca responde <c>pago_excede_saldo_pendiente</c>; sí exige que la compra tenga saldo pendiente.
    ///
    /// Orden de locks: 1) compra <c>FOR UPDATE</c> → 2) pagos existentes <c>FOR UPDATE</c> en id ascendente → 3) por cada
    /// pago existente que se adopta (sin proveedor), la fila del proveedor, adentro de ese mismo paso y ANTES del insert
    /// del pago nuevo (<see cref="EscribirPagoAProveedorAsync"/>) → 4) insert del pago nuevo (sin lock propio) → 5) fila
    /// del proveedor del pago nuevo (ya tomada si hubo adopción) → 6) advisory de tesorería de la empresa, ÚLTIMO.
    ///
    /// Con <c>CerrarEnCero</c> la diferencia entre el saldo y lo que paga este pedido (crédito aplicado + líneas nuevas) se
    /// cierra a mano si no supera la tolerancia <c>tolerancia_redondeo_pago_proveedor</c> de la compra: si faltó pagar, un
    /// redondeo del lado de la compra la deja Pagada; si se pagó de más, el excedente del pago nuevo es un redondeo del lado
    /// del pago (disponible 0) en vez de crédito a favor. Pasada la tolerancia es <c>409 redondeo_excede_tolerancia</c> sin
    /// escribir nada. El redondeo solo toca la fila del proveedor, que en ese punto ya está tomada (o no hay tesorería
    /// que tomar después), así que no cambia el orden de locks.
    ///
    /// <c>fecha</c> se valida siempre (no futura, en la zona del punto de venta) y es la fecha de negocio de lo que crea
    /// este pedido: el pago nuevo y las aplicaciones nuevas de los pagos existentes. <c>concepto</c> es del pago nuevo:
    /// sin líneas se rechaza (<c>400 concepto_sin_pago_nuevo</c>) en vez de descartarlo. Si el crédito existente salda la
    /// compra y además vienen líneas, no hay dónde aplicar el pago nuevo: <c>409 compra_sin_saldo_pendiente</c> sin escribir.
    /// update-404-first: la compra y cada pago existente inexistentes responden 404 antes de validar el cuerpo del
    /// pedido. Forma (b) de ef-retry-safe-writes.
    /// </summary>
    public async Task<ResultadoDePagoDeCompraConAplicaciones> PagarCompraConAplicacionesAsync(
        int idCompra, ComandoDePagoDeCompraConAplicaciones solicitud, CancellationToken ct = default)
    {
        var idTenant = ExigirTenantDeLaSesion();
        var idEmpleado = contexto.UsuarioId;
        var momento = reloj.Ahora;

        var idPuntoVentaDeLaCompra = await db.ComprobantesCompra
            .Where(c => c.Id == idCompra)
            .Select(c => (int?)c.IdPuntoVenta)
            .FirstOrDefaultAsync(ct)
            ?? throw ErrorDominio.NoEncontrado($"No existe la compra {idCompra}.");
        await ExigirPagosPedidosExistentesAsync(solicitud.Aplicaciones, ct);

        var existentes = ExigirAplicacionesDePagosExistentes(solicitud.Aplicaciones);
        var plan = solicitud.Lineas is { Count: > 0 } ? ReglaDeLineasDePago.Planificar(solicitud.Lineas) : null;
        if (plan is null && existentes.Count == 0)
        {
            throw new ErrorDominio("pago_sin_lineas", "Indicá al menos una línea de pago o un pago a aplicar.", 400);
        }

        var concepto = string.IsNullOrWhiteSpace(solicitud.Concepto) ? null : solicitud.Concepto.Trim();
        if (plan is null && concepto is not null)
        {
            throw new ErrorDominio(
                "concepto_sin_pago_nuevo", "El concepto es del pago nuevo: sin líneas de pago no hay a qué ponérselo.", 400);
        }

        var puntoVenta = await ResolverPuntoVentaAsync(idPuntoVentaDeLaCompra, ct);
        var zona = await ResolverZonaAsync(puntoVenta.IdEmpresa, puntoVenta.Id, ct);
        ExigirFechaNoFutura(solicitud.Fecha, momento, zona);
        var fechaDeNegocio = FechaDeNegocioAInstante(solicitud.Fecha, zona, momento);

        decimal? toleranciaDeRedondeo = solicitud.CerrarEnCero
            ? await RedondeoDeProveedor.ResolverToleranciaAsync(db, puntoVenta.IdEmpresa, puntoVenta.Id, ct)
            : null;

        var estrategia = FabricaDeEstrategiaSinReintento.CrearEstrategiaSinReintento(db);
        return await estrategia.ExecuteAsync(async () =>
            await EjecutarPagoDeCompraConAplicacionesAsync(
                idTenant, idCompra, solicitud.Fecha, fechaDeNegocio, puntoVenta, plan, concepto, existentes,
                toleranciaDeRedondeo, idEmpleado, momento, ct));
    }

    /// <summary>update-404-first: cada <c>idPago</c> pedido que no existe es 404 antes de validar el cuerpo. Una lista
    /// con más pagos que el máximo se deja pasar: la validación del cuerpo la rechaza con 400 sin recorrerla.</summary>
    private async Task ExigirPagosPedidosExistentesAsync(
        IReadOnlyList<AplicacionDePagoExistente?>? pedidas, CancellationToken ct)
    {
        if (pedidas is null || pedidas.Count > MaximoDeAplicacionesPorPedido)
        {
            return;
        }

        foreach (var idPago in pedidas.Where(a => a is not null).Select(a => a!.IdPago).Distinct().Order())
        {
            await ExigirPagoExistenteAsync(idPago, ct);
        }
    }

    private async Task<ResultadoDePagoDeCompraConAplicaciones> EjecutarPagoDeCompraConAplicacionesAsync(
        int idTenant, int idCompra, DateOnly fecha, DateTimeOffset fechaDeNegocio, PuntoVenta puntoVenta,
        PlanDeLineasDePago? plan, string? concepto, IReadOnlyList<AplicacionDePagoExistente> existentes,
        decimal? toleranciaDeRedondeo, int idEmpleado, DateTimeOffset momento, CancellationToken ct)
    {
        await using var transaccion = await db.Database.BeginTransactionAsync(ct);

        // Solo el pago nuevo entra al libro: aplicar pagos existentes no cambia lo que el libro lee. La marca de conciliación es el
        // primer lock (ver GuardaDeConciliacion).
        if (plan is not null)
        {
            await GuardaDeConciliacion.ExigirNoConciliadoAsync(db, idTenant, puntoVenta.IdEmpresa, fechaDeNegocio, ct);
        }

        var compra = await BloquearCompraAsync(idCompra, idTenant, ct);

        var idsDeLosPagos = existentes.Select(a => a.IdPago).Order().ToList();
        foreach (var idPago in idsDeLosPagos)
        {
            await TomarLockDePagoAsync(idPago, idTenant, ct);
        }

        // Única lectura de cada pago y de sus aplicaciones, después de todos los locks.
        var pagos = idsDeLosPagos.Count == 0
            ? []
            : await db.Pagos.Where(p => idsDeLosPagos.Contains(p.Id)).OrderBy(p => p.Id).ToListAsync(ct);
        var aplicacionesPrevias = idsDeLosPagos.Count == 0
            ? []
            : await db.AplicacionesPago.Where(a => idsDeLosPagos.Contains(a.IdPago)).ToListAsync(ct);
        var redondeosPrevios = await LectorDeDisponiblePorPago.LeerRedondeoAsync(db, idsDeLosPagos, ct);

        var pagado = (await LectorDePagadoPorCompra.LeerAsync(db, [idCompra], ct)).GetValueOrDefault(idCompra, 0m);
        var saldo = ReglaDePagoDeCompra.SaldoPendiente(EstadoCompra.Confirmada, compra.Total, pagado);
        if (saldo <= 0m)
        {
            throw new ErrorDominio("compra_sin_saldo_pendiente", "La compra no tiene saldo pendiente de pago.", 409);
        }

        foreach (var pedida in existentes.OrderBy(a => a.IdPago))
        {
            var pago = pagos.First(p => p.Id == pedida.IdPago);
            ExigirPagoSinPagoDePersonal(pago.IdMovimientoPersonal);
            if (pago.Categoria == CategoriaPago.Sueldos)
            {
                throw new ErrorDominio("pago_no_aplicable", "Un pago de sueldos no se aplica a compras.", 409);
            }

            await ExigirCompraDeLaEmpresaAsync(compra, pago.IdEmpresa, ct);

            if (pago.IdProveedor is { } idProveedorDelPago && idProveedorDelPago != compra.IdProveedor)
            {
                throw new ErrorDominio(
                    "compra_de_otro_proveedor", "La compra indicada es de otro proveedor que el del pago.", 409);
            }

            var aplicado = aplicacionesPrevias.Where(a => a.IdPago == pago.Id).Sum(a => a.Importe);
            if (pedida.Importe > LectorDeDisponiblePorPago.Disponible(
                    pago.Importe, aplicado, redondeosPrevios.GetValueOrDefault(pago.Id, 0m)))
            {
                throw new ErrorDominio(
                    "aplicacion_excede_disponible", "El importe a aplicar supera lo disponible del pago.", 409);
            }
        }

        var totalExistente = existentes.Sum(a => a.Importe);
        if (totalExistente > saldo)
        {
            throw new ErrorDominio(
                "aplicacion_excede_saldo", "El importe a aplicar supera el saldo pendiente de la compra.", 409);
        }

        if (plan is not null && totalExistente == saldo)
        {
            throw new ErrorDominio("compra_sin_saldo_pendiente", "La compra no tiene saldo pendiente de pago.", 409);
        }

        // Cerrar en cero: la diferencia que deja este pedido (positiva si falta pagar, negativa si se paga de más) tiene que
        // entrar en la tolerancia ANTES de escribir nada.
        var diferencia = saldo - totalExistente - (plan?.Total ?? 0m);
        if (toleranciaDeRedondeo is { } tolerancia && diferencia != 0m
            && !ReglaDeRedondeoDePago.EstaEnTolerancia(Math.Abs(diferencia), tolerancia))
        {
            throw new ErrorDominio(
                "redondeo_excede_tolerancia",
                $"La diferencia de {Math.Abs(diferencia):0.00} supera la tolerancia de redondeo de {tolerancia:0.00}.", 409);
        }

        foreach (var pedida in existentes.OrderBy(a => a.IdPago))
        {
            var pago = pagos.First(p => p.Id == pedida.IdPago);
            var delPago = aplicacionesPrevias.Where(a => a.IdPago == pago.Id).ToList();
            var redondeoDelPagoExistente = redondeosPrevios.GetValueOrDefault(pago.Id, 0m);
            var valorAnterior = PayloadDeAplicacion(pago, delPago, redondeoDelPagoExistente);

            var adopta = pago.Categoria != CategoriaPago.Proveedor || pago.IdProveedor is null;
            var existente = delPago.FirstOrDefault(a => a.IdComprobanteCompra == idCompra);
            if (existente is not null)
            {
                existente.Importe += pedida.Importe;
                existente.UpdatedAt = momento;
            }
            else
            {
                var nueva = new AplicacionPago
                {
                    IdTenant = idTenant,
                    IdPago = pago.Id,
                    IdComprobanteCompra = idCompra,
                    Importe = pedida.Importe,
                    Origen = OrigenAplicacionPago.Aplicacion,
                    IdEmpleado = idEmpleado,
                    Fecha = fechaDeNegocio,
                    CreatedAt = momento,
                    UpdatedAt = momento
                };
                db.AplicacionesPago.Add(nueva);
                delPago.Add(nueva);
            }

            if (adopta)
            {
                pago.Categoria = CategoriaPago.Proveedor;
                pago.IdProveedor = compra.IdProveedor;
                pago.IdComprobanteCompra = idCompra;
            }

            pago.UpdatedAt = momento;
            await db.SaveChangesAsync(ct);

            if (adopta)
            {
                await EscribirPagoAProveedorAsync(
                    idTenant, compra.IdProveedor, pago.IdPuntoVenta ?? compra.IdPuntoVenta, pago.Importe, pago,
                    idEmpleado, momento, ct);
            }

            auditoria.Registrar(new RegistroDeAuditoria(
                idTenant, pago.IdPuntoVenta, AccionAuditada.PagoAplicacion, pago.Id, valorAnterior,
                PayloadDeAplicacion(pago, delPago, redondeoDelPagoExistente)));
            await db.SaveChangesAsync(ct);
        }

        Pago? pagoNuevo = null;
        if (plan is not null)
        {
            await ExigirMediosDeLasLineasValidosAsync(plan.Lineas.Select(l => l.IdMedioPago), ct);

            concepto ??= await ConceptoPorDefectoDelPagoAsync(idCompra, ct);

            // La aplicación del pago nuevo la topea AplicarPagoALaCompraAsync contra lo ya aplicado a la compra, que
            // incluye lo que se acaba de guardar de los pagos existentes.
            pagoNuevo = await PersistirPagoDeAdministracionAsync(
                idTenant, puntoVenta.IdEmpresa, fechaDeNegocio,
                new ComandoDePagoDeAdministracion(
                    fecha, puntoVenta.IdEmpresa, puntoVenta.Id, CategoriaPago.Proveedor, compra.IdProveedor,
                    IdArea: null, concepto, Detalle: null, plan.Lineas, NumeroFactura: null, idCompra),
                plan, compra.IdProveedor, compra, idEmpleado, momento, ct);
        }

        var aplicaciones = await db.AplicacionesPago
            .Where(a => a.IdComprobanteCompra == idCompra)
            .OrderBy(a => a.Id)
            .Select(a => new AplicacionALaCompraListada(a.Id, a.IdPago, a.Importe, a.Fecha))
            .ToListAsync(ct);

        var aplicadoDelPagoNuevo = pagoNuevo is null
            ? 0m
            : aplicaciones.Where(a => a.IdPago == pagoNuevo.Id).Sum(a => a.Importe);

        // Cierre en cero: lo que falta de la compra es un redondeo del lado de la compra; lo que sobró del pago nuevo es un
        // redondeo del lado del pago. La tolerancia ya se validó contra la diferencia calculada de antemano.
        decimal? redondeo = null;
        var redondeoDelPagoNuevo = 0m;
        if (toleranciaDeRedondeo is not null && diferencia > 0m)
        {
            await RedondearCompraAsync(idTenant, idCompra, compra, diferencia, idEmpleado, momento, ct);
            redondeo = diferencia;
        }
        else if (toleranciaDeRedondeo is not null && diferencia < 0m)
        {
            redondeoDelPagoNuevo = pagoNuevo!.Importe - aplicadoDelPagoNuevo;
            await RedondearPagoAsync(idTenant, pagoNuevo, compra.IdProveedor, compra.IdPuntoVenta, redondeoDelPagoNuevo, idEmpleado, momento, ct);
            redondeo = diferencia;
        }

        await transaccion.CommitAsync(ct);

        var pagadoFinal = (await LectorDePagadoPorCompra.LeerAsync(db, [idCompra], ct)).GetValueOrDefault(idCompra, 0m);

        return new ResultadoDePagoDeCompraConAplicaciones(
            pagoNuevo is null ? null : Proyectar(pagoNuevo, plan!.IdMedioPagoDeCompatibilidad),
            pagadoFinal,
            ReglaDePagoDeCompra.SaldoPendiente(EstadoCompra.Confirmada, compra.Total, pagadoFinal),
            aplicaciones,
            pagoNuevo is null ? null : pagoNuevo.Importe - aplicadoDelPagoNuevo - redondeoDelPagoNuevo,
            redondeo);
    }

    /// <summary>dto-contract-honesty: cada entrada se usa o se rechaza. De 0 a 20, sin nulos, sin pagos repetidos y con
    /// importes positivos de hasta dos decimales.</summary>
    private static IReadOnlyList<AplicacionDePagoExistente> ExigirAplicacionesDePagosExistentes(
        IReadOnlyList<AplicacionDePagoExistente?>? pedidas)
    {
        if (pedidas is null || pedidas.Count == 0)
        {
            return [];
        }

        if (pedidas.Count > MaximoDeAplicacionesPorPedido)
        {
            throw new ErrorDominio(
                "aplicacion_demasiados_pagos",
                $"Se pueden aplicar hasta {MaximoDeAplicacionesPorPedido} pagos por pedido.", 400);
        }

        if (pedidas.Any(a => a is null || !ReglaDeLineasDePago.EsImporteValido(a.Importe)))
        {
            throw new ErrorDominio(
                "aplicacion_importe_invalido",
                "Cada pago a aplicar tiene que indicar un importe positivo y con hasta dos decimales.", 400);
        }

        if (pedidas.Select(a => a!.IdPago).Distinct().Count() != pedidas.Count)
        {
            throw new ErrorDominio("aplicacion_pago_repetido", "Un pago no puede figurar dos veces.", 400);
        }

        return pedidas!;
    }

    // ---- lecturas de pagos aplicables --------------------------------------------------------------------------

    /// <summary>
    /// Pagos con importe disponible que se pueden aplicar a una compra: los de su proveedor y los pagos sin proveedor,
    /// siempre de la misma empresa que la compra (aplicar exige esa empresa; los pagos sin proveedor adoptarían al
    /// proveedor al aplicarse). Quedan fuera los de sueldos y los que nacieron de un pago de personal.
    /// </summary>
    public async Task<PagosAplicables> ListarPagosAplicablesDeLaCompraAsync(int idCompra, CancellationToken ct = default)
    {
        var compra = await db.ComprobantesCompra
            .Where(c => c.Id == idCompra)
            .Select(c => new
            {
                c.IdProveedor,
                c.IdPuntoVenta,
                IdEmpresa = db.PuntosVenta.Where(pv => pv.Id == c.IdPuntoVenta).Select(pv => pv.IdEmpresa).FirstOrDefault()
            })
            .FirstOrDefaultAsync(ct)
            ?? throw ErrorDominio.NoEncontrado($"No existe la compra {idCompra}.");

        var tolerancia = await RedondeoDeProveedor.ResolverToleranciaAsync(db, compra.IdEmpresa, compra.IdPuntoVenta, ct);

        var pagos = await PagosConDisponibleAsync(
            db.Pagos.Where(p =>
                p.IdMovimientoPersonal == null
                && p.IdEmpresa == compra.IdEmpresa
                && ((p.Categoria == CategoriaPago.Proveedor && p.IdProveedor == compra.IdProveedor)
                    || (p.IdProveedor == null && p.Categoria != CategoriaPago.Sueldos))),
            ct);

        return pagos with { ToleranciaRedondeo = tolerancia };
    }

    /// <summary>
    /// Pagos de un proveedor con importe disponible, el crédito a favor que se puede aplicar a sus compras. Lista los de
    /// todas las empresas del proveedor: al aplicar, la compra tiene que ser de la empresa del pago.
    /// </summary>
    public async Task<PagosAplicables> ListarPagosDisponiblesDelProveedorAsync(int idProveedor, CancellationToken ct = default)
    {
        if (!await db.Proveedores.AnyAsync(p => p.Id == idProveedor, ct))
        {
            throw ErrorDominio.NoEncontrado($"No existe el proveedor {idProveedor}.");
        }

        return await PagosConDisponibleAsync(
            db.Pagos.Where(p => p.Categoria == CategoriaPago.Proveedor && p.IdProveedor == idProveedor), ct);
    }

    /// <summary>Tope de filas de las listas de pagos aplicables: los más antiguos primero, para que una cola larga de
    /// pagos con disponible no cargue toda la tabla; el resto se alcanza aplicando y volviendo a consultar.</summary>
    private const int MaximoDePagosAplicables = 200;

    private async Task<PagosAplicables> PagosConDisponibleAsync(IQueryable<Pago> candidatos, CancellationToken ct)
    {
        // El disponible > 0 se resuelve en SQL (importe menos las aplicaciones vivas, que el filtro de baja lógica ya
        // limita) y el tope se aplica después de filtrar.
        var filas = await candidatos
            .Where(p => p.Importe
                - (db.AplicacionesPago.Where(a => a.IdPago == p.Id).Sum(a => (decimal?)a.Importe) ?? 0m)
                - (db.MovimientosCuentaCorrienteProveedor
                    .Where(m => m.Tipo == TipoMovimientoCcProveedor.Redondeo && m.IdPago == p.Id)
                    .Sum(m => (decimal?)m.Importe) ?? 0m) > 0m)
            .OrderBy(p => p.Fecha).ThenBy(p => p.Id)
            .Take(MaximoDePagosAplicables)
            .Select(p => new { p.Id, p.Fecha, p.Concepto, p.Importe, p.IdProveedor })
            .ToListAsync(ct);

        var idsDeLasFilas = filas.Select(f => f.Id).ToList();
        var aplicado = await LectorDeDisponiblePorPago.LeerAplicadoAsync(db, idsDeLasFilas, ct);
        var redondeo = await LectorDeDisponiblePorPago.LeerRedondeoAsync(db, idsDeLasFilas, ct);

        return new PagosAplicables(filas
            .Select(f => new PagoAplicable(
                f.Id, f.Fecha, f.Concepto, f.Importe,
                LectorDeDisponiblePorPago.Disponible(
                    f.Importe, aplicado.GetValueOrDefault(f.Id, 0m), redondeo.GetValueOrDefault(f.Id, 0m)),
                f.IdProveedor))
            .Where(p => p.Disponible > 0m)
            .ToList());
    }
}
