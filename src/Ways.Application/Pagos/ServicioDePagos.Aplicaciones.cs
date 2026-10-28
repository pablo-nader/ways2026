using Microsoft.EntityFrameworkCore;
using Ways.Application.Abstracciones;
using Ways.Application.Compras;
using Ways.Domain.Auditoria;
using Ways.Domain.Common;
using Ways.Domain.Compras;
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
        var disponible = LectorDeDisponiblePorPago.Disponible(pago.Importe, aplicaciones.Sum(a => a.Importe));
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

        var valorAnterior = PayloadDeAplicacion(pago, aplicaciones);

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
            PayloadDeAplicacion(pago, aplicaciones)));
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
    /// cualquier otra cosa. Orden de locks: compra <c>FOR UPDATE</c> → pago <c>FOR UPDATE</c>.
    /// </summary>
    public async Task<PagoDeAdministracionListado> DesaplicarAsync(
        int idPago, int idAplicacion, CancellationToken ct = default)
    {
        var idTenant = ExigirTenantDeLaSesion();
        var momento = reloj.Ahora;

        await ExigirPagoExistenteAsync(idPago, ct);
        var idCompra = await db.AplicacionesPago
            .Where(a => a.Id == idAplicacion && a.IdPago == idPago)
            .Select(a => (int?)a.IdComprobanteCompra)
            .FirstOrDefaultAsync(ct)
            ?? throw ErrorDominio.NoEncontrado($"No existe la aplicación {idAplicacion} del pago {idPago}.");

        var estrategia = FabricaDeEstrategiaSinReintento.CrearEstrategiaSinReintento(db);
        await estrategia.ExecuteAsync(async () =>
            await EjecutarDesaplicarAsync(idTenant, idPago, idAplicacion, idCompra, momento, ct));

        return await ObtenerDeAdministracionAsync(idPago, ct);
    }

    private async Task<int> EjecutarDesaplicarAsync(
        int idTenant, int idPago, int idAplicacion, int idCompra, DateTimeOffset momento, CancellationToken ct)
    {
        await using var transaccion = await db.Database.BeginTransactionAsync(ct);

        // Una compra anulada ya liberó sus aplicaciones (la lectura de abajo daría 404): el lock no exige confirmada.
        await BloquearCompraAsync(idCompra, idTenant, ct, exigirConfirmada: false);
        await TomarLockDePagoAsync(idPago, idTenant, ct);
        var pago = await db.Pagos.FirstAsync(p => p.Id == idPago, ct);

        // Única lectura de la fila, bajo los dos locks: si otro escritor la liberó entre el chequeo y el lock, es 404.
        var aplicacion = await db.AplicacionesPago.FirstOrDefaultAsync(a => a.Id == idAplicacion && a.IdPago == idPago, ct)
            ?? throw ErrorDominio.NoEncontrado($"No existe la aplicación {idAplicacion} del pago {idPago}.");

        var anteriores = await db.AplicacionesPago.Where(a => a.IdPago == idPago).ToListAsync(ct);
        var valorAnterior = PayloadDeAplicacion(pago, anteriores);

        aplicacion.DeletedAt = momento;
        aplicacion.UpdatedAt = momento;

        pago.UpdatedAt = momento;

        auditoria.Registrar(new RegistroDeAuditoria(
            idTenant, pago.IdPuntoVenta, AccionAuditada.PagoDesaplicacion, pago.Id, valorAnterior,
            PayloadDeAplicacion(pago, anteriores.Where(a => a.Id != idAplicacion).ToList())));
        await db.SaveChangesAsync(ct);

        await transaccion.CommitAsync(ct);

        return pago.Id;
    }

    private static Dictionary<string, object?> PayloadDeAplicacion(Pago pago, IReadOnlyCollection<AplicacionPago> aplicaciones)
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
            ["disponible"] = LectorDeDisponiblePorPago.Disponible(pago.Importe, aplicado),
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
                IdEmpresa = db.PuntosVenta.Where(pv => pv.Id == c.IdPuntoVenta).Select(pv => pv.IdEmpresa).FirstOrDefault()
            })
            .FirstOrDefaultAsync(ct)
            ?? throw ErrorDominio.NoEncontrado($"No existe la compra {idCompra}.");

        return await PagosConDisponibleAsync(
            db.Pagos.Where(p =>
                p.IdMovimientoPersonal == null
                && p.IdEmpresa == compra.IdEmpresa
                && ((p.Categoria == CategoriaPago.Proveedor && p.IdProveedor == compra.IdProveedor)
                    || (p.IdProveedor == null && p.Categoria != CategoriaPago.Sueldos))),
            ct);
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
            .Where(p => p.Importe - (db.AplicacionesPago.Where(a => a.IdPago == p.Id).Sum(a => (decimal?)a.Importe) ?? 0m) > 0m)
            .OrderBy(p => p.Fecha).ThenBy(p => p.Id)
            .Take(MaximoDePagosAplicables)
            .Select(p => new { p.Id, p.Fecha, p.Concepto, p.Importe, p.IdProveedor })
            .ToListAsync(ct);

        var aplicado = await LectorDeDisponiblePorPago.LeerAplicadoAsync(db, filas.Select(f => f.Id).ToList(), ct);

        return new PagosAplicables(filas
            .Select(f => new PagoAplicable(
                f.Id, f.Fecha, f.Concepto, f.Importe,
                LectorDeDisponiblePorPago.Disponible(f.Importe, aplicado.GetValueOrDefault(f.Id, 0m)), f.IdProveedor))
            .Where(p => p.Disponible > 0m)
            .ToList());
    }
}
