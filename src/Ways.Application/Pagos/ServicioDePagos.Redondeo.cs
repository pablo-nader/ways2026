using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Storage;
using Ways.Application.Abstracciones;
using Ways.Application.Compras;
using Ways.Domain.Auditoria;
using Ways.Domain.Common;
using Ways.Domain.Compras;
using Ways.Domain.Pagos;

namespace Ways.Application.Pagos;

/// <summary>
/// E4: cierre en cero por redondeo. Perdona lo que falta pagar de una compra (lado compra) o absorbe lo que queda disponible
/// de un pago (lado pago) cuando la diferencia no supera la tolerancia <c>tolerancia_redondeo_pago_proveedor</c>; la
/// escritura y sus signos viven en <see cref="RedondeoDeProveedor"/>. El tercer camino, pagar una compra con
/// <c>cerrarEnCero</c>, está en <see cref="PagarCompraConAplicacionesAsync"/>.
/// Orden de locks de los dos ajustes explícitos: compra (o pago) <c>FOR UPDATE</c> → fila del proveedor; nunca tocan la
/// tesorería, porque no mueve plata: solo la deuda.
/// </summary>
public partial class ServicioDePagos
{
    /// <summary>
    /// Cierra en cero una compra confirmada con saldo pendiente de hasta la tolerancia: escribe un redondeo del lado de la
    /// compra por todo el saldo, que la deja Pagada. Sin saldo es <c>409 compra_sin_saldo_pendiente</c>; con más saldo que la
    /// tolerancia, <c>409 redondeo_excede_tolerancia</c>; una compra anulada o en borrador, el 409 de siempre.
    /// update-404-first: la compra inexistente es 404 antes de todo. Forma (b) de ef-retry-safe-writes: escribe el movimiento
    /// y el saldo del proveedor sin clave de idempotencia.
    /// </summary>
    public async Task<ResultadoDeRedondeoDeCompra> AjustarRedondeoDeCompraAsync(int idCompra, CancellationToken ct = default)
    {
        var idTenant = ExigirTenantDeLaSesion();
        var idEmpleado = contexto.UsuarioId;
        var momento = reloj.Ahora;

        var idPuntoVenta = await db.ComprobantesCompra
            .Where(c => c.Id == idCompra)
            .Select(c => (int?)c.IdPuntoVenta)
            .FirstOrDefaultAsync(ct)
            ?? throw ErrorDominio.NoEncontrado($"No existe la compra {idCompra}.");
        var puntoVenta = await ResolverPuntoVentaAsync(idPuntoVenta, ct);
        var tolerancia = await RedondeoDeProveedor.ResolverToleranciaAsync(db, puntoVenta.IdEmpresa, puntoVenta.Id, ct);

        var estrategia = FabricaDeEstrategiaSinReintento.CrearEstrategiaSinReintento(db);
        return await estrategia.ExecuteAsync(async () =>
            await EjecutarAjusteDeRedondeoDeCompraAsync(idTenant, idCompra, tolerancia, idEmpleado, momento, ct));
    }

    private async Task<ResultadoDeRedondeoDeCompra> EjecutarAjusteDeRedondeoDeCompraAsync(
        int idTenant, int idCompra, decimal tolerancia, int idEmpleado, DateTimeOffset momento, CancellationToken ct)
    {
        await using var transaccion = await db.Database.BeginTransactionAsync(ct);

        var compra = await BloquearCompraAsync(idCompra, idTenant, ct);

        // Única lectura de lo pagado, después del lock de la compra.
        var pagado = (await LectorDePagadoPorCompra.LeerAsync(db, [idCompra], ct)).GetValueOrDefault(idCompra, 0m);
        var saldo = ReglaDePagoDeCompra.SaldoPendiente(EstadoCompra.Confirmada, compra.Total, pagado);
        if (saldo <= 0m)
        {
            throw new ErrorDominio("compra_sin_saldo_pendiente", "La compra no tiene saldo pendiente de pago.", 409);
        }

        ExigirDentroDeLaTolerancia(saldo, tolerancia);

        await RedondearCompraAsync(idTenant, idCompra, compra, saldo, idEmpleado, momento, ct);
        await transaccion.CommitAsync(ct);

        return new ResultadoDeRedondeoDeCompra(idCompra, saldo, compra.Total, 0m);
    }

    /// <summary>
    /// Cierra en cero lo que queda disponible de un pago de proveedor, si no supera la tolerancia: escribe un redondeo del
    /// lado del pago por todo el disponible, que queda en 0. Un pago que no es de un proveedor es
    /// <c>409 pago_sin_proveedor</c>; sin disponible, <c>409 pago_sin_disponible</c>; con más que la tolerancia,
    /// <c>409 redondeo_excede_tolerancia</c>. update-404-first. Forma (b) de ef-retry-safe-writes.
    /// </summary>
    public async Task<ResultadoDeRedondeoDePago> AjustarRedondeoDePagoAsync(int idPago, CancellationToken ct = default)
    {
        var idTenant = ExigirTenantDeLaSesion();
        var idEmpleado = contexto.UsuarioId;
        var momento = reloj.Ahora;

        var datos = await db.Pagos
            .Where(p => p.Id == idPago)
            .Select(p => new { p.IdEmpresa, p.IdPuntoVenta })
            .FirstOrDefaultAsync(ct)
            ?? throw ErrorDominio.NoEncontrado($"No existe el gasto {idPago}.");
        var tolerancia = await RedondeoDeProveedor.ResolverToleranciaAsync(db, datos.IdEmpresa, datos.IdPuntoVenta, ct);

        var estrategia = FabricaDeEstrategiaSinReintento.CrearEstrategiaSinReintento(db);
        return await estrategia.ExecuteAsync(async () =>
            await EjecutarAjusteDeRedondeoDePagoAsync(idTenant, idPago, tolerancia, idEmpleado, momento, ct));
    }

    private async Task<ResultadoDeRedondeoDePago> EjecutarAjusteDeRedondeoDePagoAsync(
        int idTenant, int idPago, decimal tolerancia, int idEmpleado, DateTimeOffset momento, CancellationToken ct)
    {
        await using var transaccion = await db.Database.BeginTransactionAsync(ct);

        await TomarLockDePagoAsync(idPago, idTenant, ct);
        var pago = await db.Pagos.FirstAsync(p => p.Id == idPago, ct);
        ExigirPagoSinPagoDePersonal(pago.IdMovimientoPersonal);

        if (pago.Categoria != CategoriaPago.Proveedor || pago.IdProveedor is not { } idProveedor)
        {
            throw new ErrorDominio("pago_sin_proveedor", "Solo un pago a un proveedor tiene disponible para redondear.", 409);
        }

        var aplicado = (await LectorDeDisponiblePorPago.LeerAplicadoAsync(db, [idPago], ct)).GetValueOrDefault(idPago, 0m);
        var redondeo = (await LectorDeDisponiblePorPago.LeerRedondeoAsync(db, [idPago], ct)).GetValueOrDefault(idPago, 0m);
        var disponible = LectorDeDisponiblePorPago.Disponible(pago.Importe, aplicado, redondeo);
        if (disponible <= 0m)
        {
            throw new ErrorDominio("pago_sin_disponible", "El pago no tiene importe disponible para redondear.", 409);
        }

        ExigirDentroDeLaTolerancia(disponible, tolerancia);

        var idPuntoVenta = pago.IdPuntoVenta
            ?? await PuntoVentaDeLaCompraAsync(pago.IdComprobanteCompra, ct)
            ?? await PuntoVentaDeUnaAplicacionAsync(idPago, ct)
            ?? throw new ErrorDominio(
                "pago_sin_punto_de_venta", "El pago no tiene un punto de venta del que tomar el movimiento.", 409);

        await RedondearPagoAsync(idTenant, pago, idProveedor, idPuntoVenta, disponible, idEmpleado, momento, ct);
        await transaccion.CommitAsync(ct);

        return new ResultadoDeRedondeoDePago(idPago, disponible, 0m);
    }

    private async Task<int?> PuntoVentaDeUnaAplicacionAsync(int idPago, CancellationToken ct) =>
        await db.AplicacionesPago
            .Where(a => a.IdPago == idPago)
            .OrderBy(a => a.Id)
            .Select(a => db.ComprobantesCompra.Where(c => c.Id == a.IdComprobanteCompra).Select(c => (int?)c.IdPuntoVenta).FirstOrDefault())
            .FirstOrDefaultAsync(ct);

    private static void ExigirDentroDeLaTolerancia(decimal diferencia, decimal tolerancia)
    {
        if (!ReglaDeRedondeoDePago.EstaEnTolerancia(diferencia, tolerancia))
        {
            throw new ErrorDominio(
                "redondeo_excede_tolerancia",
                $"La diferencia de {diferencia:0.00} supera la tolerancia de redondeo de {tolerancia:0.00}.", 409);
        }
    }

    // ---- escrituras compartidas con el pago de una compra, la edición, la baja y la desaplicación --------------------

    /// <summary>Redondeo del lado de la compra: perdona <paramref name="perdonado"/> (positivo) de su saldo. El llamador tiene
    /// la compra bloqueada y confirmada y ya validó la tolerancia.</summary>
    private async Task RedondearCompraAsync(
        int idTenant, int idCompra, CompraBloqueada compra, decimal perdonado, int idEmpleado, DateTimeOffset momento,
        CancellationToken ct)
    {
        var conexion = await ObtenerConexionAbiertaAsync(ct);
        var transaccionCruda = db.Database.CurrentTransaction?.GetDbTransaction();

        await RedondeoDeProveedor.RegistrarAsync(
            conexion, transaccionCruda, idTenant, compra.IdProveedor, momento, compra.IdPuntoVenta, idEmpleado, idCompra,
            idPago: null, -perdonado, $"Redondeo de la compra #{idCompra}", ct);

        auditoria.Registrar(new RegistroDeAuditoria(
            idTenant, compra.IdPuntoVenta, AccionAuditada.CompraRedondeo, idCompra,
            new Dictionary<string, object?> { ["redondeo"] = 0m },
            new Dictionary<string, object?> { ["redondeo"] = perdonado, ["saldo_pendiente"] = 0m }));
        await db.SaveChangesAsync(ct);
    }

    /// <summary>Redondeo del lado del pago: absorbe <paramref name="absorbido"/> (positivo) de su disponible. El llamador tiene
    /// el pago bloqueado, ya validó la tolerancia y escribe sobre un proveedor cuya fila toma este mismo método.</summary>
    private async Task RedondearPagoAsync(
        int idTenant, Pago pago, int idProveedor, int idPuntoVentaDeRespaldo, decimal absorbido, int idEmpleado,
        DateTimeOffset momento, CancellationToken ct)
    {
        var conexion = await ObtenerConexionAbiertaAsync(ct);
        var transaccionCruda = db.Database.CurrentTransaction?.GetDbTransaction();
        var idPuntoVenta = pago.IdPuntoVenta ?? idPuntoVentaDeRespaldo;

        await RedondeoDeProveedor.RegistrarAsync(
            conexion, transaccionCruda, idTenant, idProveedor, momento, idPuntoVenta, idEmpleado, idCompra: null, pago.Id,
            absorbido, $"Redondeo del pago #{pago.Id}", ct);

        auditoria.Registrar(new RegistroDeAuditoria(
            idTenant, pago.IdPuntoVenta, AccionAuditada.PagoRedondeo, pago.Id,
            new Dictionary<string, object?> { ["redondeo"] = 0m },
            new Dictionary<string, object?> { ["redondeo"] = absorbido, ["disponible"] = 0m }));
        await db.SaveChangesAsync(ct);
    }

    /// <summary>Compensa el redondeo vigente de una compra que pierde aplicaciones (desaplicar, baja o recorte de un pago). Si no
    /// tenía, no escribe nada. El llamador tiene la compra bloqueada; este método toma después la fila del proveedor.</summary>
    private async Task RevertirRedondeoDeLaCompraAsync(
        int idTenant, int idCompra, CompraBloqueada compra, int idEmpleado, DateTimeOffset momento, string motivo,
        CancellationToken ct)
    {
        var conexion = await ObtenerConexionAbiertaAsync(ct);
        var transaccionCruda = db.Database.CurrentTransaction?.GetDbTransaction();

        var revertido = await RedondeoDeProveedor.RevertirDeLaCompraAsync(
            db, conexion, transaccionCruda, idTenant, compra.IdProveedor, idCompra, compra.IdPuntoVenta, idEmpleado, momento,
            motivo, ct);
        if (revertido == 0m)
        {
            return;
        }

        auditoria.Registrar(new RegistroDeAuditoria(
            idTenant, compra.IdPuntoVenta, AccionAuditada.CompraRedondeo, idCompra,
            new Dictionary<string, object?> { ["redondeo"] = revertido },
            new Dictionary<string, object?> { ["redondeo"] = 0m, ["motivo"] = motivo }));
        await db.SaveChangesAsync(ct);
    }

    /// <summary>
    /// Compensaciones del redondeo en la edición y la baja de un pago, con el pago y todas sus compras bloqueados: (1) con
    /// la baja, un cambio del importe o un cambio de proveedor o de categoría (el pago deja la cuenta en la que se
    /// absorbió la diferencia), el redondeo del lado del pago se revierte y el disponible vuelve a ser importe −
    /// aplicado; (2) cada compra confirmada a la que el pago aplica menos que antes revierte su redondeo del lado de la
    /// compra, porque perdonaba una diferencia que dependía de esa aplicación. Una compra a la que se aplica igual o más
    /// no se toca. Corre después de las escrituras de aplicaciones y antes de la tesorería, que va última.
    /// </summary>
    private async Task CompensarElRedondeoAsync(
        int idTenant, Pago pago, decimal importeAnterior, IReadOnlyDictionary<int, decimal> aplicadoAntesPorCompra,
        IReadOnlyDictionary<int, CompraBloqueada> comprasBloqueadas, bool esBaja, bool cambiaLaCuenta, string motivo,
        int idEmpleado, DateTimeOffset momento, CancellationToken ct)
    {
        if (esBaja || cambiaLaCuenta || pago.Importe != importeAnterior)
        {
            await RevertirRedondeoDelPagoAsync(idTenant, pago, idEmpleado, momento, motivo, ct);
        }

        if (aplicadoAntesPorCompra.Count == 0)
        {
            return;
        }

        // El filtro global de baja lógica deja afuera las aplicaciones liberadas.
        var aplicadoDespuesPorCompra = await db.AplicacionesPago
            .Where(a => a.IdPago == pago.Id)
            .GroupBy(a => a.IdComprobanteCompra)
            .Select(g => new { IdCompra = g.Key, Total = g.Sum(a => a.Importe) })
            .ToDictionaryAsync(g => g.IdCompra, g => g.Total, ct);

        foreach (var (idCompra, antes) in aplicadoAntesPorCompra.OrderBy(a => a.Key))
        {
            if (aplicadoDespuesPorCompra.GetValueOrDefault(idCompra, 0m) < antes
                && comprasBloqueadas.TryGetValue(idCompra, out var compra)
                && compra.Confirmada)
            {
                await RevertirRedondeoDeLaCompraAsync(idTenant, idCompra, compra, idEmpleado, momento, motivo, ct);
            }
        }
    }

    /// <summary>Compensa el redondeo vigente de un pago que se edita en su importe o se da de baja: el disponible vuelve a ser
    /// importe − aplicado. Si no tenía, no escribe nada. El llamador tiene el pago bloqueado.</summary>
    private async Task RevertirRedondeoDelPagoAsync(
        int idTenant, Pago pago, int idEmpleado, DateTimeOffset momento, string motivo, CancellationToken ct)
    {
        var conexion = await ObtenerConexionAbiertaAsync(ct);
        var transaccionCruda = db.Database.CurrentTransaction?.GetDbTransaction();

        var revertido = await RedondeoDeProveedor.RevertirDelPagoAsync(
            db, conexion, transaccionCruda, idTenant, pago.Id, idEmpleado, momento, motivo, ct);
        if (revertido == 0m)
        {
            return;
        }

        auditoria.Registrar(new RegistroDeAuditoria(
            idTenant, pago.IdPuntoVenta, AccionAuditada.PagoRedondeo, pago.Id,
            new Dictionary<string, object?> { ["redondeo"] = revertido },
            new Dictionary<string, object?> { ["redondeo"] = 0m, ["motivo"] = motivo }));
        await db.SaveChangesAsync(ct);
    }
}
