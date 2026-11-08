using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Storage;
using Ways.Application.Abstracciones;
using Ways.Application.Auditoria;
using Ways.Application.Caja;
using Ways.Application.CuentaCorriente;
using Ways.Application.Plantel;
using Ways.Domain.Auditoria;
using Ways.Domain.Common;
using Ways.Domain.CuentaCorriente;
using Ways.Domain.Pagos;
using Ways.Domain.Plantel;

namespace Ways.Application.Pagos;

public partial class ServicioDePagos
{
    /// <summary>
    /// Corrige las líneas de dinero del pago de un asiento del personal (el medio o el reparto) sin cambiar su total, así que el
    /// libro del personal y el descuento de cuenta corriente quedan como están. Todo lo demás del pago (categoría, concepto,
    /// origen, importe) tampoco cambia. Es el mismo camino que <see cref="EditarDeAdministracionAsync"/>, con su orden de locks
    /// (<see cref="EjecutarEscrituraAsync"/>), el recálculo del arqueo de un turno cerrado, la compensación de tesorería, los
    /// débitos en cuentas y la auditoría <c>pago.edicion</c>; lo único distinto es la guarda: exige un pago de personal en vez
    /// de rechazarlo, y que bajo su lock siga siendo el del mismo asiento.
    /// update-404-first: el pago inexistente es 404 antes de validar el cuerpo; un pago común es 409
    /// <c>pago_sin_movimiento_de_personal</c>. Que las líneas sumen el importe del pago se decide sobre el pago leído bajo su
    /// lock (400 <c>importe_distinto_del_pago</c>). Forma (b) de ef-retry-safe-writes, igual que la edición administrativa.
    /// </summary>
    public async Task<PagoDeAdministracionListado> EditarLineasDePagoDePersonalAsync(
        int idPago, SolicitudDeLineasDePagoDePersonal? solicitud, CancellationToken ct = default)
    {
        var idTenant = ExigirTenantDeLaSesion();
        var idEmpleado = contexto.UsuarioId;
        var momento = reloj.Ahora;

        var inmutables = await LeerDatosInmutablesAsync(idPago, ct);
        if (inmutables.IdMovimientoPersonal is null)
        {
            throw PagoSinMovimientoDePersonal();
        }

        var plan = ReglaDeLineasDePago.Planificar(solicitud?.Lineas);

        var estrategia = FabricaDeEstrategiaSinReintento.CrearEstrategiaSinReintento(db);
        await estrategia.ExecuteAsync(async () =>
            await EjecutarEscrituraAsync(
                idTenant, idPago, inmutables, solicitud: null, plan, ModoDeEscritura.LineasDePersonal, idEmpleado, momento,
                ct));

        return await ObtenerDeAdministracionAsync(idPago, ct);
    }

    private sealed record PagoDelAsiento(int Id, int IdEmpresa, int? IdTurnoCaja, int? IdPuntoVenta);

    private sealed record DescuentoDelAsiento(int IdCliente, decimal Importe, int IdPuntoVenta);

    /// <summary>
    /// Anula un pago de personal entero en una sola transacción. El libro es append-only, así que la anulación es un asiento
    /// <c>ajuste</c> por el total del pago (el opuesto de su importe), con la fecha del pago y <c>id_movimiento_anulado</c>
    /// apuntándolo; si hubo descuento, la deuda vuelve al mismo cliente con un <c>ajuste</c> de cuenta corriente en el punto de
    /// venta del descuento; y cada pago del asiento (un asiento anterior a E2-P puede tener uno por línea) se da de baja por el
    /// mismo camino que <see cref="EliminarDeAdministracionAsync"/>: recálculo del arqueo de un turno cerrado, ajuste de
    /// tesorería, débitos en cuentas y auditoría <c>pago.baja</c>. Deja además el rastro <c>pagopersonal.anulacion</c>.
    /// Un personal o un asiento inexistente, de otro tenant o de otro personal es 404; un asiento que no es un pago es 409
    /// <c>movimiento_no_es_pago</c>; uno ya anulado, 409 <c>pago_personal_ya_anulado</c> (el chequeo previo es la respuesta
    /// rápida, el definitivo va bajo el lock del personal y el índice único parcial es el respaldo).
    /// Forma (b) de ef-retry-safe-writes: mueve saldos y escribe asientos sin clave de idempotencia, y un reintento tras un
    /// commit ambiguo anularía dos veces o respondería 409 a una anulación que sí tuvo éxito.
    ///
    /// Orden de locks, en este orden y sin excepciones:
    /// 1) marca de conciliación compartida de la empresa de cada pago (si hay pagos), en id ascendente →
    /// 2) turno de cada pago <c>FOR UPDATE</c>, en id ascendente (el estado leído bajo el lock decide el recálculo) →
    /// 3) fila del personal <c>FOR UPDATE</c> →
    /// 4) asiento del pago <c>FOR UPDATE</c>, con la anulación previa revisada bajo el lock →
    /// 5) fila del cliente del descuento (<c>UPDATE ... RETURNING</c> que exige <c>deleted_at IS NULL</c>; sin fila viva, 409
    ///    <c>cliente_del_descuento_dado_de_baja</c>) →
    /// 6) cada pago <c>FOR UPDATE</c>, en id ascendente, TODOS antes de dar de baja el primero →
    /// 7) inserts sin lock explícito (asiento de anulación, ajuste de cuenta corriente) y, por cada pago, filas de
    ///    <c>arqueos_turno</c> y marca del turno (cubiertas por el lock 2), advisory de tesorería de la empresa y cuentas de
    ///    tesorería, ÚLTIMOS.
    ///
    /// Análisis de ciclo. El pago de personal (<c>ServicioDePagoDePersonal</c>) toma conciliación, turno, personal, cliente y
    /// advisory en el mismo orden relativo. La reasignación toma personal, asiento y pago, el mismo orden relativo que 3 → 4 →
    /// 6, y no toca turnos ni el advisory. La edición y la baja genéricas toman conciliación, turno, compras, pago y advisory,
    /// rechazan un pago de personal antes de la transacción y, si llegaran a lockearlo, lo rechazan bajo su lock sin pedir nada
    /// más; nunca toman el personal ni el cliente. La corrección de líneas toma conciliación, turno, pago y advisory: comparte
    /// con esta anulación el turno, que las dos piden antes que el pago, y un pago sin turno solo se cruza en el paso 6, que
    /// acá ocurre antes del advisory; por eso los pagos se lockean todos juntos y no uno por baja. La conversión de un pago
    /// común toma turno, pago y personal: el pago que lockea no tiene asiento, así que nunca es uno de los de esta anulación, y
    /// si comparten turno se serializan en el paso 2. El cierre de turno toma el turno exclusivo y después el advisory, el
    /// mismo orden relativo que 2 → 7. Las ventas y los cobros toman turno y cliente; la carga de horas, el ajuste y la baja
    /// del personal, solo la fila del personal. Un pago de sueldos no tiene compras ni aplicaciones (las rechazan su
    /// categoría y la guarda de pago de personal), así que la baja de cada pago no lockea ninguna compra después del paso 6.
    /// </summary>
    public async Task<MovimientoDePersonal> AnularPagoDePersonalAsync(
        int idPersonal, int idMovimiento, CancellationToken ct = default)
    {
        var idTenant = ExigirTenantDeLaSesion();
        var idEmpleado = contexto.UsuarioId;
        var momento = reloj.Ahora;

        var nombre = await db.Personal
            .Where(p => p.Id == idPersonal)
            .Select(p => p.Nombre)
            .FirstOrDefaultAsync(ct)
            ?? throw SinPersonal(idPersonal);

        var original = await db.MovimientosPersonal
            .Where(m => m.Id == idMovimiento && m.IdPersonal == idPersonal)
            .Select(m => new { m.Tipo, m.Fecha })
            .FirstOrDefaultAsync(ct)
            ?? throw ErrorDominio.NoEncontrado($"No existe el movimiento {idMovimiento} del personal {idPersonal}.");

        if (original.Tipo != TipoMovimientoPersonal.Pago)
        {
            throw ErrorDominio.Conflicto("movimiento_no_es_pago", "Solo se anula un movimiento de tipo pago.");
        }

        if (await EstaAnuladoAsync(idMovimiento, ct))
        {
            throw PagoDePersonalYaAnulado();
        }

        // El vínculo con el asiento se escribe solo al insertar el pago o al convertir uno común (que crea un asiento nuevo), así
        // que el conjunto de pagos de este asiento no crece; bajo el lock de cada pago se confirma que siga atado a él.
        var pagos = await db.Pagos
            .Where(g => g.IdMovimientoPersonal == idMovimiento)
            .OrderBy(g => g.Id)
            .Select(g => new PagoDelAsiento(g.Id, g.IdEmpresa, g.IdTurnoCaja, g.IdPuntoVenta))
            .ToListAsync(ct);

        var estrategia = FabricaDeEstrategiaSinReintento.CrearEstrategiaSinReintento(db);
        return await estrategia.ExecuteAsync(async () =>
            await EjecutarAnulacionAsync(
                idTenant, idPersonal, nombre, idMovimiento, original.Fecha, pagos, idEmpleado, momento, ct));
    }

    private async Task<MovimientoDePersonal> EjecutarAnulacionAsync(
        int idTenant, int idPersonal, string nombre, int idMovimiento, DateOnly fecha, IReadOnlyList<PagoDelAsiento> pagos,
        int idEmpleado, DateTimeOffset momento, CancellationToken ct)
    {
        await using var transaccion = await db.Database.BeginTransactionAsync(ct);

        foreach (var idEmpresa in pagos.Select(p => p.IdEmpresa).Distinct().Order())
        {
            await GuardaDeConciliacion.TomarCompartidoAsync(db, idTenant, idEmpresa, ct);
        }

        var turnosCerrados = new Dictionary<int, TurnoBloqueado>();
        foreach (var idTurno in pagos.Select(p => p.IdTurnoCaja).OfType<int>().Distinct().Order())
        {
            var turno = await BloquearTurnoAsync(idTurno, idTenant, ModoDeEscritura.Administracion, ct);
            if (!turno.Abierto)
            {
                turnosCerrados[idTurno] = turno;
            }
        }

        var conexion = await ObtenerConexionAbiertaAsync(ct);
        var transaccionCruda = db.Database.CurrentTransaction?.GetDbTransaction();

        if (await EscriturasDePersonal.BloquearPersonalAsync(conexion, transaccionCruda, idTenant, idPersonal, ct) is null)
        {
            throw SinPersonal(idPersonal);
        }

        var movimiento = await EscriturasDePersonal.BloquearMovimientoAsync(
            conexion, transaccionCruda, idTenant, idMovimiento, ct);
        if (movimiento is null || movimiento.IdPersonal != idPersonal)
        {
            throw PagoCambioConcurrente();
        }

        if (await EstaAnuladoAsync(idMovimiento, ct))
        {
            throw PagoDePersonalYaAnulado();
        }

        var descuento = await db.MovimientosCuentaCorriente
            .Where(m => m.IdMovimientoPersonal == idMovimiento)
            .Select(m => new DescuentoDelAsiento(m.IdCliente, m.Importe, m.IdPuntoVenta))
            .FirstOrDefaultAsync(ct);

        var plan = ReglaDeAnulacionDePagoDePersonal.Planificar(movimiento.Importe, fecha, descuento?.Importe);

        decimal? saldoDelCliente = null;
        if (descuento is not null)
        {
            saldoDelCliente = await EscriturasDeCuentaCorriente.ActualizarSaldoDeClienteVivoAsync(
                conexion, transaccionCruda, idTenant, descuento.IdCliente, plan.DeudaDevuelta!.Value, ct)
                ?? throw ErrorDominio.Conflicto(
                    "cliente_del_descuento_dado_de_baja",
                    "El cliente al que se le descontó la deuda está dado de baja; no se le puede devolver.");
        }

        foreach (var pago in pagos)
        {
            await TomarLockDePagoAsync(pago.Id, idTenant, ct);
        }

        var saldo = await EscriturasDePersonal.ActualizarSaldoAsync(
            conexion, transaccionCruda, idTenant, idPersonal, plan.ImporteDelAjuste, ct)
            ?? throw SinPersonal(idPersonal);

        var idAnulacion = await EscriturasDePersonal.InsertarAnulacionDePagoAsync(
            conexion, transaccionCruda, idTenant, idPersonal, idEmpleado, fecha, plan.ImporteDelAjuste, saldo, plan.Detalle,
            idMovimiento, momento, ct);

        if (descuento is not null)
        {
            await EscriturasDeCuentaCorriente.InsertarMovimientoCcAsync(
                conexion, transaccionCruda, idTenant, descuento.IdCliente, momento, descuento.IdPuntoVenta, idEmpleado,
                TipoMovimientoCc.Ajuste, idComprobanteVenta: null, idCobro: null, plan.DeudaDevuelta!.Value,
                saldoDelCliente!.Value, $"Anulación del descuento por pago de personal: {nombre}", ct);
        }

        foreach (var pago in pagos)
        {
            var turnoCerrado = pago.IdTurnoCaja is { } idTurno ? turnosCerrados.GetValueOrDefault(idTurno) : null;
            await EscribirBajoElLockDelTurnoAsync(
                idTenant, pago.Id, turnoCerrado, solicitud: null, plan: null, idMovimientoPersonalEsperado: idMovimiento,
                idEmpleado, momento, ct);
        }

        var idsDePagos = pagos.Select(p => p.Id).ToList();
        auditoria.Registrar(new RegistroDeAuditoria(
            idTenant, pagos.FirstOrDefault()?.IdPuntoVenta ?? descuento?.IdPuntoVenta,
            AccionAuditada.PagoDePersonalAnulacion, idMovimiento,
            new Dictionary<string, object?>
            {
                ["id_personal"] = idPersonal,
                ["saldo"] = saldo - plan.ImporteDelAjuste,
                ["id_pagos"] = idsDePagos,
                ["id_cliente"] = descuento?.IdCliente,
                ["descuento"] = plan.DeudaDevuelta,
                ["saldo_del_cliente"] = saldoDelCliente - plan.DeudaDevuelta
            },
            new Dictionary<string, object?>
            {
                ["id_personal"] = idPersonal,
                ["saldo"] = saldo,
                ["id_pagos"] = Array.Empty<int>(),
                ["id_cliente"] = descuento?.IdCliente,
                ["descuento"] = null,
                ["saldo_del_cliente"] = saldoDelCliente,
                ["id_movimiento_anulacion"] = idAnulacion,
                ["importe_de_la_anulacion"] = plan.ImporteDelAjuste
            }));
        await db.SaveChangesAsync(ct);

        await transaccion.CommitAsync(ct);

        return new MovimientoDePersonal(
            idAnulacion, fecha, TipoMovimientoPersonal.Ajuste, Horas: null, ValorHora: null, plan.ImporteDelAjuste, saldo,
            plan.Detalle, momento, IdMovimientoAnulado: idMovimiento);
    }

    private Task<bool> EstaAnuladoAsync(int idMovimiento, CancellationToken ct) =>
        db.MovimientosPersonal.AnyAsync(m => m.IdMovimientoAnulado == idMovimiento, ct);

    private static ErrorDominio PagoDePersonalYaAnulado() =>
        ErrorDominio.Conflicto("pago_personal_ya_anulado", "El pago ya está anulado.");

    private static ErrorDominio PagoSinMovimientoDePersonal() =>
        ErrorDominio.Conflicto("pago_sin_movimiento_de_personal", "El pago no es un pago de sueldo de un empleado.");

    private static ErrorDominio SinPersonal(int idPersonal) =>
        ErrorDominio.NoEncontrado($"No existe el personal {idPersonal}.");
}
