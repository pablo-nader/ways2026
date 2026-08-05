using System.Data;
using System.Data.Common;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Storage;
using Ways.Application.Abstracciones;
using Ways.Application.Caja;
using Ways.Application.Ventas;
using Ways.Domain.Catalogos;
using Ways.Domain.Clientes;
using Ways.Domain.Common;
using Ways.Domain.CuentaCorriente;
using Ways.Domain.Organizacion;
using Ways.Domain.Ventas;

namespace Ways.Application.CuentaCorriente;

/// <summary>
/// Pago a cuenta (RC) — servicio dedicado y lean (design decisión 1: "no threadea
/// <c>EmitirAsync</c>"), no una extensión de <see cref="ServicioDeVentas"/>: una RC no tiene
/// líneas, ni ofertas, ni CC como medio, así que reusar el checkout completo significaría abrir
/// seis ramas nuevas en la transacción más guardada del proyecto para un flujo que no las
/// necesita. Reusa, tal cual, las tres piezas que sí le sirven:
/// <see cref="AsignadorDeNumeroComprobante"/> (numeración, design decisión 7),
/// <see cref="ServicioDeTurnos.ExigirTurnoAbiertoBajoLockAsync"/> (guard de turno, mismo criterio
/// que <c>ServicioDeVentas.EjecutarTransaccionAsync</c> paso 0) y
/// <see cref="CuentaCorriente.EscriturasDeCuentaCorriente"/> (los dos statements que son la única
/// autoridad sobre <c>clientes.saldo</c>/<c>movimientos_cuenta_corriente</c>).
/// </summary>
public class ServicioDeCuentaCorriente(
    IWaysDbContext db, IRelojDelSistema reloj, IContextoDeUsuario contexto, ServicioDeTurnos servicioDeTurnos)
{
    /// <summary>Design: Transactions — PAGO A CUENTA (orden de statements pineado). La mitad que
    /// decide (cliente, punto de venta, turno, medios, validación) corre AFUERA de la
    /// transacción de escritura — mismo criterio que <c>ServicioDeVentas.EmitirAsync</c> — y la
    /// numeración se reserva y comitea en su PROPIA transacción, ANTES de la que escribe el
    /// resto (design decisión 7).</summary>
    public async Task<ComprobanteEmitido> RegistrarPagoAsync(
        int idCliente, SolicitudDePagoACuenta solicitud, CancellationToken ct = default)
    {
        var idTenant = ExigirTenantDeLaSesion();
        var idEmpleado = contexto.UsuarioId;
        var momento = reloj.Ahora;

        var cliente = await ResolverClienteAsync(idCliente, ct);
        if (cliente.EsConsumidorFinal)
        {
            throw new ErrorDominio(
                "cliente_sin_cuenta_corriente", "El Consumidor Final no tiene cuenta corriente.", 400);
        }

        var puntoVenta = await ResolverPuntoVentaAsync(solicitud.IdPuntoVenta, ct);

        // Turno resuelto server-side, ANTES de cualquier otro trabajo de negocio (spec: RC
        // Requires An Open Turno — "rejected before any other processing") — mismo criterio que
        // ServicioDeVentas.EmitirAsync.
        var turno = await servicioDeTurnos.ResolverTurnoAbiertoAsync(puntoVenta.Id, ct);

        var tipo = await ResolverTipoRcAsync(ct);

        var pagos = solicitud.Pagos ?? [];
        var idsMedioPago = pagos.Select(p => p.IdMedioPago).Distinct().ToList();
        var medioPorId = await db.MediosPago
            .Where(m => idsMedioPago.Contains(m.Id))
            .ToDictionaryAsync(m => m.Id, ct);

        var idsMedioFaltantes = idsMedioPago.Except(medioPorId.Keys).ToList();
        if (idsMedioFaltantes.Count > 0)
        {
            throw new ErrorDominio("referencia_invalida", $"No existe el medio de pago {idsMedioFaltantes[0]}.", 400);
        }

        var vueltoMaximo = await ResolverParametroAsync(ParametroConocido.VueltoMaximo, puntoVenta.IdEmpresa, puntoVenta.Id, ct);

        var pagosAValidar = pagos
            .Select(p =>
            {
                var medio = medioPorId[p.IdMedioPago];
                return new PagoAValidar(
                    p.IdMedioPago, medio.Comportamiento, medio.AdmiteVuelto, medio.RequiereReferencia,
                    p.Importe, p.Vuelto, p.Referencia);
            })
            .ToList();

        var importeAplicado = ValidadorDePagoACuenta.Validar(pagosAValidar, vueltoMaximo);

        // Misma corrección que ServicioDeVentas.EmitirAsync: el número se reserva y COMITEA en su
        // propia transacción, ANTES de la que escribe el resto — "se consume aunque falle el
        // resto" es literal, no una aproximación.
        var estrategiaNumeracion = db.Database.CreateExecutionStrategy();
        var numero = await estrategiaNumeracion.ExecuteAsync(async () =>
            await AsignadorDeNumeroComprobante.AsignarComprometidoAsync(db, idTenant, puntoVenta.Id, tipo.Codigo, ct));

        // Sin reintento automático (mismo criterio que ServicioDeVentas.AnularAsync): un pago a
        // cuenta es manual, sin clave de idempotencia propia — a diferencia de EmitirAsync, no
        // hay ningún BuscarPorNumeroComprometidoAsync que detecte un commit ambiguo previo antes
        // de reinsertar.
        var estrategia = FabricaDeEstrategiaSinReintento.CrearEstrategiaSinReintento(db);
        return await estrategia.ExecuteAsync(async () =>
            await EjecutarTransaccionAsync(
                idTenant, idEmpleado, momento, tipo.Id, numero, puntoVenta.Id, turno.Id, cliente.Id, importeAplicado,
                pagos, ct));
    }

    private async Task<ComprobanteEmitido> EjecutarTransaccionAsync(
        int idTenant, int idEmpleado, DateTimeOffset momento, int idTipoComprobante, long numero, int idPuntoVenta,
        int idTurnoCaja, int idCliente, decimal importeAplicado, IReadOnlyList<PagoDeCuenta> pagos,
        CancellationToken ct)
    {
        await using var transaccion = await db.Database.BeginTransactionAsync(ct);

        // 1. Turno — re-chequeo bajo FOR SHARE, PRIMER statement (mismo criterio que
        // ServicioDeVentas.EjecutarTransaccionAsync paso 0): el turno ya vino resuelto como
        // abierto arriba, ANTES de esta transacción.
        await servicioDeTurnos.ExigirTurnoAbiertoBajoLockAsync(idTurnoCaja, ct);

        // 2. Comprobante — cero items por construcción (afecta_stock = false de RC, design:
        // Table Shapes B). Subtotal/DescuentoTotal no tienen concepto propio sin líneas: se
        // igualan al total, sin descuento.
        var comprobante = new ComprobanteVenta
        {
            IdTipoComprobante = idTipoComprobante,
            Numero = numero,
            Fecha = momento,
            IdPuntoVenta = idPuntoVenta,
            IdTurnoCaja = idTurnoCaja,
            IdEmpleado = idEmpleado,
            IdCliente = idCliente,
            Subtotal = importeAplicado,
            DescuentoTotal = 0m,
            Total = importeAplicado,
            Observaciones = null,
            Estado = EstadoComprobante.Emitido,
            CreatedAt = momento,
            UpdatedAt = momento
        };
        db.ComprobantesVenta.Add(comprobante);
        await db.SaveChangesAsync(ct);

        // 3. Pagos — sin items, sin movimientos_stock (spec: RC Carries Zero Items And No Stock
        // Effect).
        var pagosEntidad = pagos
            .Select(p => new PagoComprobante
            {
                IdComprobanteVenta = comprobante.Id,
                IdMedioPago = p.IdMedioPago,
                Importe = p.Importe,
                Referencia = p.Referencia,
                Vuelto = p.Vuelto,
                CreatedAt = momento,
                UpdatedAt = momento
            })
            .ToList();
        db.PagosComprobante.AddRange(pagosEntidad);
        await db.SaveChangesAsync(ct);

        var conexion = await ObtenerConexionAbiertaAsync(ct);
        var transaccionCruda = db.Database.CurrentTransaction?.GetDbTransaction();

        // 4. Cuenta corriente — un único movimiento Pago, negativo (reduce la deuda).
        var nuevoSaldo = await EscriturasDeCuentaCorriente.ActualizarSaldoClienteAsync(
            conexion, transaccionCruda, idTenant, idCliente, -importeAplicado, ct);

        // 5. Movimiento — id_pago_comprobante siempre NULL en un Pago (design decisión 1/5): a
        // diferencia de un Consumo, no hay UN pago físico que "lo generó" — lo genera el
        // comprobante entero, que puede traer varios medios.
        await EscriturasDeCuentaCorriente.InsertarMovimientoCcAsync(
            conexion, transaccionCruda, idTenant, idCliente, momento, idPuntoVenta, idEmpleado,
            TipoMovimientoCc.Pago, comprobante.Id, null, -importeAplicado, nuevoSaldo, ct);

        await transaccion.CommitAsync(ct);

        return Proyectar(comprobante, pagosEntidad);
    }

    // ---- Resolución de datos, fuera de la transacción ----------------------------------------

    private async Task<Cliente> ResolverClienteAsync(int idCliente, CancellationToken ct) =>
        await db.Clientes.FirstOrDefaultAsync(c => c.Id == idCliente, ct)
            // ADR-8: mismo 404 para "no existe" y "es de otro tenant" (filtro de EF + RLS ya deja
            // invisible un cliente ajeno) — mismo criterio que ServicioDeVentas.ResolverClienteAsync.
            ?? throw ErrorDominio.NoEncontrado($"No existe el cliente {idCliente}.");

    private async Task<PuntoVenta> ResolverPuntoVentaAsync(int idPuntoVenta, CancellationToken ct) =>
        await db.PuntosVenta.FirstOrDefaultAsync(pv => pv.Id == idPuntoVenta, ct)
            ?? throw ErrorDominio.NoEncontrado($"No existe el punto de venta {idPuntoVenta}.");

    private async Task<TipoComprobante> ResolverTipoRcAsync(CancellationToken ct) =>
        await db.TiposComprobante.FirstOrDefaultAsync(t => t.Codigo == "RC", ct)
            // Sembrado idempotente para todo tenant desde la migración de Slice 1 — su ausencia
            // es un bug de aprovisionamiento, no un caso de negocio alcanzable (mismo criterio
            // que el Consumidor Final de ServicioDeVentas.ResolverClienteAsync).
            ?? throw new InvalidOperationException("El tenant actual no tiene el tipo de comprobante RC sembrado.");

    private async Task<decimal> ResolverParametroAsync(
        ParametroConocido conocido, int idEmpresa, int idPuntoVenta, CancellationToken ct)
    {
        var candidatos = await db.Parametros
            .Where(p => p.Clave == conocido.Clave && p.IdEmpresa == idEmpresa
                && (p.IdPuntoVenta == null || p.IdPuntoVenta == idPuntoVenta))
            .ToListAsync(ct);

        var valorJson = ResolucionDeParametros.Resolver(conocido.Clave, candidatos, idPuntoVenta);
        return JsonSerializer.Deserialize<decimal>(valorJson);
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

    private int ExigirTenantDeLaSesion() =>
        contexto.IdTenant
            ?? throw new InvalidOperationException(
                "ServicioDeCuentaCorriente requiere un actor de tenant; OperacionDePos no admite plataforma.");

    private static ComprobanteEmitido Proyectar(
        ComprobanteVenta comprobante, IReadOnlyList<PagoComprobante> pagos) => new(
        comprobante.Id, comprobante.Numero,
        NumeroDeComprobante.Formatear(comprobante.IdPuntoVenta, comprobante.Numero),
        comprobante.Estado, comprobante.Fecha, comprobante.IdPuntoVenta, comprobante.IdCliente,
        comprobante.IdComprobanteAsociado, comprobante.Subtotal, comprobante.DescuentoTotal, comprobante.Total,
        comprobante.DireccionEntrega, comprobante.Observaciones,
        [],
        pagos
            .Select(p => new PagoEmitido(p.IdMedioPago, p.Importe, p.Referencia, p.Vuelto))
            .ToList());
}
