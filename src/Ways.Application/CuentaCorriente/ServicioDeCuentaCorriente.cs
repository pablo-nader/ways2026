using System.Data;
using System.Data.Common;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Storage;
using Ways.Application.Abstracciones;
using Ways.Application.Caja;
using Ways.Application.Exportacion;
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
                pagos, NormalizarOpcional(solicitud.Observaciones), ct));
    }

    /// <summary>Design: Transactions — AJUSTE MANUAL (orden pineado: "fuera:
    /// ReglaDeAjusteDeCuenta … ; cliente ; punto de venta"). Una sola transacción de dos
    /// statements — sin turno (mismo criterio que la reliquidación: un ajuste no mueve plata
    /// física).</summary>
    public async Task<MovimientoDeCuentaCorriente> RegistrarAjusteAsync(
        int idCliente, SolicitudDeAjuste solicitud, CancellationToken ct = default)
    {
        var idTenant = ExigirTenantDeLaSesion();
        var idEmpleado = contexto.UsuarioId;
        var momento = reloj.Ahora;

        // 1. ReglaDeAjusteDeCuenta — pura, corre ANTES de cualquier consulta a la base (spec:
        // Ajuste with no detalle is rejected — "rejected … before any write").
        ReglaDeAjusteDeCuenta.Validar(solicitud.Importe, solicitud.Detalle);
        var detalleNormalizado = solicitud.Detalle!.Trim();

        // 2. Cliente — mismo guard CF que RegistrarPagoAsync/ServicioDeReliquidacion (un ajuste
        // manual tampoco tiene sentido de negocio sobre el Consumidor Final, que no maneja
        // cuenta corriente).
        var cliente = await ResolverClienteAsync(idCliente, ct);
        if (cliente.EsConsumidorFinal)
        {
            throw new ErrorDominio(
                "cliente_sin_cuenta_corriente", "El Consumidor Final no tiene cuenta corriente.", 400);
        }

        // 3. Punto de venta — provenance, no autoridad (design: Open Questions — el ajuste no
        // tiene turno del que derivarlo).
        var puntoVenta = await ResolverPuntoVentaAsync(solicitud.IdPuntoVenta, ct);

        var estrategia = FabricaDeEstrategiaSinReintento.CrearEstrategiaSinReintento(db);
        return await estrategia.ExecuteAsync(async () =>
            await EjecutarAjusteAsync(
                idTenant, idEmpleado, momento, idCliente, puntoVenta.Id, solicitud.Importe, detalleNormalizado, ct));
    }

    private async Task<MovimientoDeCuentaCorriente> EjecutarAjusteAsync(
        int idTenant, int idEmpleado, DateTimeOffset momento, int idCliente, int idPuntoVenta, decimal importe,
        string detalle, CancellationToken ct)
    {
        await using var transaccion = await db.Database.BeginTransactionAsync(ct);
        var conexion = await ObtenerConexionAbiertaAsync(ct);
        var transaccionCruda = db.Database.CurrentTransaction?.GetDbTransaction();

        // 1. Saldo — el mismo UPDATE ... RETURNING que el resto de la cuenta corriente, lock de
        // la fila cliente.
        var nuevoSaldo = await EscriturasDeCuentaCorriente.ActualizarSaldoClienteAsync(
            conexion, transaccionCruda, idTenant, idCliente, importe, ct);

        // 2. Movimiento — id_comprobante_venta NULL (design decisión 8: la marca estructural de
        // "es un ajuste manual, no un contramovimiento de anulación").
        var idMovimiento = await EscriturasDeCuentaCorriente.InsertarMovimientoCcAsync(
            conexion, transaccionCruda, idTenant, idCliente, momento, idPuntoVenta, idEmpleado,
            TipoMovimientoCc.Ajuste, idComprobanteVenta: null, idPagoComprobante: null, importe, nuevoSaldo, detalle, ct);

        await transaccion.CommitAsync(ct);

        return new MovimientoDeCuentaCorriente(
            idMovimiento, momento, TipoMovimientoCc.Ajuste, importe, nuevoSaldo, detalle, IdComprobanteVenta: null,
            Etiqueta: EtiquetaDeAjuste.Manual);
    }

    /// <summary>Design decisión 9: header + movimientos en un único <c>GET</c>, sin lock (lectura
    /// pura) — <c>saldo_resultante</c> es la ÚNICA fuente de la corrida, nunca re-derivada.
    /// <paramref name="historico"/> gana sobre <paramref name="desde"/>/<paramref name="hasta"/>
    /// (spec: histórico returns the full ledger); si ninguno de los tres viene, aplica el default
    /// de último mes (spec: No filter returns the last month) — un <paramref name="desde"/>/
    /// <paramref name="hasta"/> explícito nunca lo pisa.</summary>
    public async Task<EstadoDeCuenta> ObtenerEstadoDeCuentaAsync(
        int idCliente, DateTimeOffset? desde, DateTimeOffset? hasta, bool historico, CancellationToken ct = default)
    {
        var cliente = await ResolverClienteAsync(idCliente, ct);

        var disponibilidad = CalculadorDeEstadoDeCuenta.CalcularDisponibilidad(
            cliente.Saldo, cliente.LimiteCredito, cliente.CreditoIlimitado);
        var header = new EstadoDeCuentaHeader(cliente.Saldo, cliente.LimiteCredito, cliente.CreditoIlimitado, disponibilidad);

        DateTimeOffset? desdeEfectivo = null;
        DateTimeOffset? hastaEfectivo = null;
        if (!historico)
        {
            // Un hasta explícito sin desde también recorta la ventana a un mes — sin este piso,
            // hasta-only devolvía TODO el ledger desde el día uno con Historico=false.
            desdeEfectivo = desde ?? (hasta is { } hastaSinDesde ? hastaSinDesde.AddMonths(-1) : reloj.Ahora.AddMonths(-1));
            hastaEfectivo = hasta;
        }

        // Newest-first (legacy: `ORDER BY fecha DESC`, doc-01:375 — "saldo corriendo hacia atrás
        // desde el saldo actual") y convención de resumen bancario. El cómputo del saldo (columna
        // saldo_resultante persistida) es ortogonal a este orden de display.
        var filas = await ConstruirQuery(idCliente, desdeEfectivo, hastaEfectivo)
            .OrderByDescending(m => m.Fecha).ThenByDescending(m => m.Id)
            .Select(m => new
            {
                m.Id, m.Fecha, m.Tipo, m.Importe, m.SaldoResultante, m.Detalle, m.IdComprobanteVenta
            })
            .ToListAsync(ct);

        var movimientos = filas
            .Select(f => new MovimientoDeCuentaCorriente(
                f.Id, f.Fecha, f.Tipo, f.Importe, f.SaldoResultante, f.Detalle, f.IdComprobanteVenta,
                f.Tipo == TipoMovimientoCc.Ajuste ? CalculadorDeEstadoDeCuenta.EtiquetarAjuste(f.IdComprobanteVenta) : null))
            .ToList();

        return new EstadoDeCuenta(header, movimientos, historico, desdeEfectivo, hastaEfectivo);
    }

    /// <summary>
    /// stage-11-exportacion-reportes (Slice 3, design decisión 7): export del ledger — sin
    /// <c>histórico</c> (una exportación es por definición un rango acotado, el tope de filas es
    /// exactamente lo que ese modo evita) y con <paramref name="desde"/>/<paramref name="hasta"/>
    /// SIEMPRE explícitos, a diferencia de <see cref="ObtenerEstadoDeCuentaAsync"/> que puede
    /// aplicar el default de último mes. Header calculado igual, ledger mismo
    /// <see cref="ConstruirQuery"/> — <c>Contar → refuse → lectura única con
    /// <c>.Take(topeDeFilas + 1)</c></c>.</summary>
    public async Task<EstadoDeCuenta> ObtenerEstadoDeCuentaParaExportacionAsync(
        int idCliente, DateTimeOffset desde, DateTimeOffset hasta, int topeDeFilas, CancellationToken ct = default)
    {
        var cliente = await ResolverClienteAsync(idCliente, ct);

        var disponibilidad = CalculadorDeEstadoDeCuenta.CalcularDisponibilidad(
            cliente.Saldo, cliente.LimiteCredito, cliente.CreditoIlimitado);
        var header = new EstadoDeCuentaHeader(cliente.Saldo, cliente.LimiteCredito, cliente.CreditoIlimitado, disponibilidad);

        var query = ConstruirQuery(idCliente, desde, hasta);

        var cantidad = await query.CountAsync(ct);
        GuardaDeTope.Exigir(cantidad, topeDeFilas);

        var filas = await query
            .OrderByDescending(m => m.Fecha).ThenByDescending(m => m.Id)
            .Take(topeDeFilas + 1)
            .Select(m => new
            {
                m.Id, m.Fecha, m.Tipo, m.Importe, m.SaldoResultante, m.Detalle, m.IdComprobanteVenta
            })
            .ToListAsync(ct);

        GuardaDeTope.Exigir(filas.Count, topeDeFilas);

        var movimientos = filas
            .Select(f => new MovimientoDeCuentaCorriente(
                f.Id, f.Fecha, f.Tipo, f.Importe, f.SaldoResultante, f.Detalle, f.IdComprobanteVenta,
                f.Tipo == TipoMovimientoCc.Ajuste ? CalculadorDeEstadoDeCuenta.EtiquetarAjuste(f.IdComprobanteVenta) : null))
            .ToList();

        return new EstadoDeCuenta(header, movimientos, Historico: false, desde, hasta);
    }

    /// <summary>Filtro compartido de <see cref="ObtenerEstadoDeCuentaAsync"/> y
    /// <see cref="ObtenerEstadoDeCuentaParaExportacionAsync"/> (design decisión 7).</summary>
    private IQueryable<MovimientoCuentaCorriente> ConstruirQuery(
        int idCliente, DateTimeOffset? desde, DateTimeOffset? hasta)
    {
        var consulta = db.MovimientosCuentaCorriente.Where(m => m.IdCliente == idCliente);

        if (desde is { } desdeAplicado)
        {
            consulta = consulta.Where(m => m.Fecha >= desdeAplicado);
        }

        if (hasta is { } hastaAplicado)
        {
            consulta = consulta.Where(m => m.Fecha <= hastaAplicado);
        }

        return consulta;
    }

    private async Task<ComprobanteEmitido> EjecutarTransaccionAsync(
        int idTenant, int idEmpleado, DateTimeOffset momento, int idTipoComprobante, long numero, int idPuntoVenta,
        int idTurnoCaja, int idCliente, decimal importeAplicado, IReadOnlyList<PagoDeCuenta> pagos,
        string? observaciones, CancellationToken ct)
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
            Observaciones = observaciones,
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
            TipoMovimientoCc.Pago, comprobante.Id, null, -importeAplicado, nuevoSaldo, detalle: null, ct);

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

    // Mismo criterio que ServicioDeVentas.NormalizarOpcional: un string en blanco no es una
    // observación, es ruido — se persiste NULL en vez de espacios.
    private static string? NormalizarOpcional(string? valor)
    {
        var limpio = valor?.Trim();
        return string.IsNullOrEmpty(limpio) ? null : limpio;
    }

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
