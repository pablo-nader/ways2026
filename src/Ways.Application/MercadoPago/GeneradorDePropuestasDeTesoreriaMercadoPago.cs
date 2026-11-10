using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Ways.Application.Abstracciones;
using Ways.Application.Auditoria;
using Ways.Application.Caja;
using Ways.Domain.Auditoria;
using Ways.Domain.Caja;
using Ways.Domain.Catalogos;
using Ways.Domain.MercadoPago;
using Ways.Domain.Ventas;

namespace Ways.Application.MercadoPago;

/// <summary>Cuántas propuestas generó una importación (solo las que se crearon), qué cobros quedaron fuera del cálculo de la comisión y cuántos retiros
/// del reporte ya tenían propuesta en otra conciliación de la empresa (o repetidos en el mismo archivo) y no se volvieron a proponer.
/// <see cref="RetirosCancelados"/> son las operaciones distintas que el reporte cancela (<c>WITHDRAWAL_CANCEL</c>); de ellas,
/// <see cref="RetirosDescartadosPorCancelacion"/> eran propuestas pendientes que se descartaron y <see cref="Reversiones"/> son las reversiones que se
/// propusieron porque el retiro ya estaba aplicado.</summary>
public sealed record PropuestasGeneradas(
    int Diferencias, int Retiros, int CobrosSinCierre, int CobrosSinComisionReal, int RetirosYaPropuestos,
    int RetirosCancelados = 0, int RetirosDescartadosPorCancelacion = 0, int Reversiones = 0);

/// <summary>
/// Genera las propuestas de tesorería de una conciliación recién importada (E7). Corre dentro de la transacción de la importación, después de
/// guardar las filas y de fijar la comisión real de los cobros conciliados, así que nace junto con la conciliación y no se vuelve a calcular: la
/// elección de calcular al importar (y no al leer) es que el cálculo ve una foto consistente del libro y de los cobros bajo el lock de la
/// empresa, y que el GET es una lectura pura. Lo que no entra: un cobro conciliado cuyo turno todavía no acreditó su medio en la cuenta (el cierre
/// lo va a descontar después con el porcentaje del medio) no tiene una comisión estimada que corregir, y un turno que se cierra DESPUÉS de importar
/// no se suma a una conciliación ya hecha; los dos casos se cuentan en el detalle y en la auditoría de la importación.
///
/// La comisión estimada de un cobro es la que descontó el cierre del turno de su venta (<see cref="CalculadorDeDiferenciaDeComisionMercadoPago"/>):
/// no se guarda por pago, así que se rearma con el porcentaje que la fila de comisión del cierre dejó para ese turno y medio, aplicado al bruto de los
/// pagos del grupo y redondeado una vez por grupo como el cierre. Solo cuentan los pagos de comprobantes emitidos (los anulados no entraron al bruto del
/// cierre). Ver <see cref="CalculadorDeDiferenciaDeComisionMercadoPago"/> para la aproximación que queda.
///
/// <b>Retiros.</b> Un retiro se propone UNA vez por empresa: su clave es el SOURCE_ID de Mercado Pago y un índice parcial único por empresa lo respalda, así
/// que dos reportes de rangos que se pisan no lo duplican (un retiro que ya tiene propuesta en cualquier conciliación de la empresa, pendiente, aplicada o
/// descartada, no se vuelve a proponer y se cuenta; el Admin puede reabrir una descartada). La cuenta de origen es la de los medios de pago con proveedor
/// Mercado Pago de la empresa cuando es una sola; si hay varias o ninguna queda nula y el Admin la elige al aplicar (<see cref="CuentasDeOrigenAsync"/>).
///
/// <b>Cancelaciones.</b> Una fila <c>WITHDRAWAL_CANCEL</c> cancela el retiro de su mismo SOURCE_ID (supuesto: el reporte repite en la cancelación el id de
/// la operación que cancela; no hay otro dato para emparejarlas). Según el estado de la propuesta de ese retiro en la empresa, que se lee bajo el lock de su
/// fila: pendiente, se descarta con el motivo "cancelado por Mercado Pago" (auditado, no mueve dinero); aplicada, se propone una reversión
/// (<see cref="TiposDePropuestaDeTesoreriaMercadoPago.RetiroCancelado"/>, una por operación y empresa) que el Admin aplica; descartada o inexistente, la
/// cancelación solo se cuenta. Si el MISMO archivo trae el retiro y su cancelación no se propone el retiro (se cuenta como cancelado): proponer y descartar
/// en el mismo paso solo dejaría ruido; vale para <c>WITHDRAWAL</c> y para <c>PAYOUT</c>. La cancelación se recuerda entre reportes: un retiro que se importa
/// DESPUÉS de una cancelación ya importada (una fila <c>WITHDRAWAL_CANCEL</c> de la empresa con su SOURCE_ID, en otra conciliación) nace descartado con el motivo
/// "cancelado por Mercado Pago". Toda propuesta que una cancelación toca (descartada, nacida descartada, retiro aplicado y su reversión, o descartada a mano)
/// queda con <see cref="PropuestaDeTesoreriaMercadoPago.CanceladoPorMercadoPago"/>, que reabrir no borra y que impide reabrirla.
///
/// <b>Diferencia de comisión y anulaciones.</b> Solo cuentan los pagos de comprobantes emitidos HOY: una venta que se anula después de cerrado el turno queda
/// fuera del cálculo aunque el cierre ya la haya acreditado (los flujos de anulación no se concilian acá; la diferencia de esa venta no se propone).
/// </summary>
public static class GeneradorDePropuestasDeTesoreriaMercadoPago
{
    public const int MaximoDeLineas = 200;

    private static readonly JsonSerializerOptions OpcionesDeJson = new(JsonSerializerDefaults.Web);

    public static string SerializarDetalle(DetalleDeDiferenciaDeComision detalle) => JsonSerializer.Serialize(detalle, OpcionesDeJson);

    public static DetalleDeDiferenciaDeComision? LeerDetalle(string? json) =>
        json is null ? null : JsonSerializer.Deserialize<DetalleDeDiferenciaDeComision>(json, OpcionesDeJson);

    /// <param name="cobrosAplicados">Los cobros de Mercado Pago que esta conciliación concilió, con la comisión real que se les guardó.</param>
    /// <param name="filasDeRetiro">Las filas guardadas de la conciliación cuyo tipo es retiro, cancelación de retiro o payout (ya con su id).</param>
    /// <param name="idUsuario">El usuario que importa: figura como quien resolvió las propuestas pendientes que una cancelación descarta.</param>
    public static async Task<PropuestasGeneradas> GenerarAsync(
        IWaysDbContext db,
        int idTenant,
        int idEmpresa,
        int idConciliacion,
        IReadOnlyDictionary<int, decimal?> cobrosAplicados,
        IReadOnlyList<FilaConciliacionMercadoPago> filasDeRetiro,
        int? idUsuario,
        ServicioDeAuditoria auditoria,
        DateTimeOffset ahora,
        CancellationToken ct)
    {
        var diferencias = await GenerarDiferenciasAsync(db, idTenant, idEmpresa, idConciliacion, cobrosAplicados, ahora, ct);
        var retiros = await GenerarRetirosAsync(db, idTenant, idEmpresa, idConciliacion, filasDeRetiro, idUsuario, auditoria, ahora, ct);
        return new PropuestasGeneradas(
            diferencias.Propuestas, retiros.Creados, diferencias.SinCierre, diferencias.SinComisionReal, retiros.YaPropuestos,
            retiros.Cancelados, retiros.Descartados, retiros.Reversiones);
    }

    /// <summary>Las cuentas activas de la empresa a las que acreditan sus medios de pago activos con proveedor Mercado Pago, por id. Es el conjunto
    /// de orígenes posibles de un retiro: una sola es el origen fijo, varias o ninguna exigen que el Admin elija.</summary>
    public static async Task<IReadOnlyList<CuentaDeOrigenPosible>> CuentasDeOrigenAsync(IWaysDbContext db, int idEmpresa, CancellationToken ct)
    {
        var filas = await (
                from m in db.MediosPago.AsNoTracking()
                join c in db.CuentasTesoreria.AsNoTracking() on m.IdCuentaTesoreria equals (int?)c.Id
                where (m.Proveedor == ProveedorDeCobro.MercadoPagoQr || m.Proveedor == ProveedorDeCobro.MercadoPagoPoint)
                      && m.Activo && (m.IdEmpresa == null || m.IdEmpresa == idEmpresa)
                      && c.IdEmpresa == idEmpresa && c.Activo
                select new { c.Id, c.Nombre })
            .Distinct()
            .OrderBy(c => c.Id)
            .ToListAsync(ct);
        return filas.Select(c => new CuentaDeOrigenPosible(c.Id, c.Nombre)).ToList();
    }

    private static async Task<(int Propuestas, int SinCierre, int SinComisionReal)> GenerarDiferenciasAsync(
        IWaysDbContext db, int idTenant, int idEmpresa, int idConciliacion, IReadOnlyDictionary<int, decimal?> cobrosAplicados,
        DateTimeOffset ahora, CancellationToken ct)
    {
        if (cobrosAplicados.Count == 0)
        {
            return (0, 0, 0);
        }

        var idsDeCobroMp = cobrosAplicados.Keys.ToList();
        var pagos = await (
                from p in db.Cobros.AsNoTracking()
                where p.IdCobroMercadoPago != null && idsDeCobroMp.Contains(p.IdCobroMercadoPago.Value) && p.DeletedAt == null
                join c in db.ComprobantesVenta.AsNoTracking().SinEspejosFiscales() on p.IdComprobanteVenta equals c.Id
                where c.Estado == EstadoComprobante.Emitido
                select new { IdCobroMercadoPago = p.IdCobroMercadoPago!.Value, IdCobro = p.Id, p.IdMedioPago, p.Importe, c.IdTurnoCaja })
            .ToListAsync(ct);

        var informadas = await ComisionesInformadasDePagos.PorPagoAsync(db, pagos.Select(p => p.IdCobro).ToList(), ct);
        var paraCalcular = pagos
            .Select(p => new PagoConciliadoParaComision(
                p.IdCobroMercadoPago, p.IdCobro, p.IdTurnoCaja, p.IdMedioPago, p.Importe, cobrosAplicados[p.IdCobroMercadoPago],
                informadas.TryGetValue(p.IdCobro, out var informada) ? informada : null))
            .ToList();

        var cierres = await LeerCierresAsync(db, paraCalcular, ct);
        var resultado = CalculadorDeDiferenciaDeComisionMercadoPago.Calcular(paraCalcular, cierres);
        var sinPago = cobrosAplicados.Count - pagos.Select(p => p.IdCobroMercadoPago).Distinct().Count();

        var lineasPorCuenta = resultado.Lineas.GroupBy(l => l.IdCuentaTesoreria).ToDictionary(g => g.Key, g => g.ToList());
        var creadas = 0;
        foreach (var cuenta in resultado.PorCuenta.Where(c => Math.Abs(c.Importe) <= ReglasDeAjusteDeCuentaTesoreria.ImporteMaximo))
        {
            creadas++;
            var lineas = lineasPorCuenta[cuenta.IdCuentaTesoreria]
                .OrderByDescending(l => Math.Abs(l.Diferencia))
                .ThenBy(l => l.IdCobro)
                .ToList();
            var detalle = new DetalleDeDiferenciaDeComision(
                cuenta.CantidadDeCobros, cuenta.ComisionRealTotal, cuenta.ComisionEstimadaTotal, resultado.CobrosSinCierre + sinPago,
                resultado.CobrosSinComisionReal, lineas.Take(MaximoDeLineas).ToList(), lineas.Count > MaximoDeLineas);

            db.PropuestasTesoreriaMercadoPago.Add(new PropuestaDeTesoreriaMercadoPago
            {
                IdTenant = idTenant,
                IdEmpresa = idEmpresa,
                IdConciliacion = idConciliacion,
                Tipo = TiposDePropuestaDeTesoreriaMercadoPago.DiferenciaComision,
                ClaveOrigen = PropuestaDeTesoreriaMercadoPago.ClaveDeCuenta(cuenta.IdCuentaTesoreria),
                Importe = cuenta.Importe,
                IdCuentaOrigen = cuenta.IdCuentaTesoreria,
                Estado = EstadosDePropuestaDeTesoreriaMercadoPago.Pendiente,
                Detalle = SerializarDetalle(detalle),
                CreatedAt = ahora,
                UpdatedAt = ahora
            });
        }

        await db.SaveChangesAsync(ct);
        return (creadas, resultado.CobrosSinCierre + sinPago, resultado.CobrosSinComisionReal);
    }

    /// <summary>Por cada (turno, medio) con fila de cobro en una cuenta: esa cuenta y el porcentaje de la fila de comisión (0 si el cierre no escribió
    /// ninguna, porque su porcentaje redondeaba a cero).</summary>
    private static async Task<Dictionary<(int IdTurnoCaja, int IdMedioPago), CierreDeMedioEnCuenta>> LeerCierresAsync(
        IWaysDbContext db, IReadOnlyList<PagoConciliadoParaComision> pagos, CancellationToken ct)
    {
        var turnos = pagos.Where(p => p.IdTurnoCaja is not null).Select(p => p.IdTurnoCaja!.Value).Distinct().ToList();
        var medios = pagos.Select(p => p.IdMedioPago).Distinct().ToList();
        if (turnos.Count == 0)
        {
            return [];
        }

        var filas = await db.MovimientosCuenta
            .AsNoTracking()
            .Where(m => m.IdTurnoCaja != null && turnos.Contains(m.IdTurnoCaja.Value) && m.IdMedioPago != null
                        && medios.Contains(m.IdMedioPago.Value)
                        && (m.Tipo == TipoMovimientoCuenta.Cobro || m.Tipo == TipoMovimientoCuenta.Comision))
            .Select(m => new { IdTurno = m.IdTurnoCaja!.Value, IdMedio = m.IdMedioPago!.Value, m.Tipo, m.IdCuentaTesoreria, m.PorcentajeAplicado })
            .ToListAsync(ct);

        var cierres = new Dictionary<(int, int), CierreDeMedioEnCuenta>();
        foreach (var cobro in filas.Where(f => f.Tipo == TipoMovimientoCuenta.Cobro))
        {
            var porcentaje = filas
                .Where(f => f.Tipo == TipoMovimientoCuenta.Comision && f.IdTurno == cobro.IdTurno && f.IdMedio == cobro.IdMedio)
                .Select(f => f.PorcentajeAplicado)
                .FirstOrDefault() ?? 0m;
            cierres[(cobro.IdTurno, cobro.IdMedio)] = new CierreDeMedioEnCuenta(cobro.IdCuentaTesoreria, porcentaje);
        }

        return cierres;
    }

    private sealed record ResultadoDeRetiros(int Creados, int YaPropuestos, int Cancelados, int Descartados, int Reversiones);

    private static async Task<ResultadoDeRetiros> GenerarRetirosAsync(
        IWaysDbContext db, int idTenant, int idEmpresa, int idConciliacion, IReadOnlyList<FilaConciliacionMercadoPago> filasDeRetiro,
        int? idUsuario, ServicioDeAuditoria auditoria, DateTimeOffset ahora, CancellationToken ct)
    {
        var cancelaciones = filasDeRetiro
            .Where(f => f.TipoTransaccion == TiposDeTransaccionMercadoPago.WithdrawalCancel && !string.IsNullOrEmpty(f.SourceId))
            .OrderBy(f => f.NumeroLinea)
            .GroupBy(f => f.SourceId!)
            .Select(g => g.First())
            .OrderBy(f => f.NumeroLinea)
            .ToList();
        var idsCancelados = cancelaciones.Select(f => f.SourceId!).ToHashSet();

        var retiros = filasDeRetiro
            .Where(f => f.TipoTransaccion is TiposDeTransaccionMercadoPago.Withdrawal or TiposDeTransaccionMercadoPago.Payout
                        && !string.IsNullOrEmpty(f.SourceId)
                        && !idsCancelados.Contains(f.SourceId!)
                        && f.Importe is { } importe && importe != 0m && Math.Abs(importe) <= ReglasDeAjusteDeCuentaTesoreria.ImporteMaximo)
            .OrderBy(f => f.NumeroLinea)
            .ToList();
        if (retiros.Count == 0 && cancelaciones.Count == 0)
        {
            return new ResultadoDeRetiros(0, 0, 0, 0, 0);
        }

        var creados = 0;
        var descartadosAlNacer = 0;
        if (retiros.Count > 0)
        {
            var claves = retiros.Select(f => PropuestaDeTesoreriaMercadoPago.ClaveDeRetiro(f.SourceId!)).Distinct().ToList();
            var yaPropuestas = (await db.PropuestasTesoreriaMercadoPago
                    .AsNoTracking()
                    .Where(p => p.IdEmpresa == idEmpresa && p.Tipo == TiposDePropuestaDeTesoreriaMercadoPago.Retiro && claves.Contains(p.ClaveOrigen))
                    .Select(p => p.ClaveOrigen)
                    .ToListAsync(ct))
                .ToHashSet();

            var cancelacionesConocidas = await CancelacionesYaImportadasAsync(
                db, idEmpresa, idConciliacion, retiros.Select(f => f.SourceId!).Distinct().ToList(), ct);
            var cuentas = await CuentasDeOrigenAsync(db, idEmpresa, ct);
            int? idCuentaDeOrigen = cuentas.Count == 1 ? cuentas[0].Id : null;

            var nacidosDescartados = new List<PropuestaDeTesoreriaMercadoPago>();
            foreach (var fila in retiros)
            {
                var clave = PropuestaDeTesoreriaMercadoPago.ClaveDeRetiro(fila.SourceId!);
                if (!yaPropuestas.Add(clave))
                {
                    continue;
                }

                var cancelado = cancelacionesConocidas.Contains(clave);
                var propuesta = new PropuestaDeTesoreriaMercadoPago
                {
                    IdTenant = idTenant,
                    IdEmpresa = idEmpresa,
                    IdConciliacion = idConciliacion,
                    Tipo = TiposDePropuestaDeTesoreriaMercadoPago.Retiro,
                    ClaveOrigen = clave,
                    Importe = Math.Abs(fila.Importe!.Value),
                    IdFila = fila.Id,
                    IdCuentaOrigen = idCuentaDeOrigen,
                    Estado = cancelado
                        ? EstadosDePropuestaDeTesoreriaMercadoPago.Descartada
                        : EstadosDePropuestaDeTesoreriaMercadoPago.Pendiente,
                    IdUsuarioResolucion = cancelado ? idUsuario : null,
                    FechaResolucion = cancelado ? ahora : null,
                    MotivoResolucion = cancelado ? PropuestaDeTesoreriaMercadoPago.MotivoCancelado : null,
                    CanceladoPorMercadoPago = cancelado,
                    CreatedAt = ahora,
                    UpdatedAt = ahora
                };
                db.PropuestasTesoreriaMercadoPago.Add(propuesta);
                if (cancelado)
                {
                    nacidosDescartados.Add(propuesta);
                }
                else
                {
                    creados++;
                }
            }

            await db.SaveChangesAsync(ct);

            foreach (var propuesta in nacidosDescartados)
            {
                auditoria.Registrar(new RegistroDeAuditoria(
                    idTenant,
                    null,
                    AccionAuditada.PropuestaDeTesoreriaDescarte,
                    propuesta.Id,
                    valorAnterior: null,
                    valorNuevo: new Dictionary<string, object?>
                    {
                        ["id_conciliacion"] = propuesta.IdConciliacion,
                        ["tipo"] = propuesta.Tipo,
                        ["importe"] = propuesta.Importe,
                        ["estado"] = propuesta.Estado,
                        ["motivo"] = propuesta.MotivoResolucion
                    }));
            }

            descartadosAlNacer = nacidosDescartados.Count;
            await db.SaveChangesAsync(ct);
        }

        var (descartados, reversiones) = await ProcesarCancelacionesAsync(
            db, idTenant, idEmpresa, idConciliacion, cancelaciones, idUsuario, auditoria, ahora, ct);
        return new ResultadoDeRetiros(
            creados, retiros.Count - creados - descartadosAlNacer, cancelaciones.Count, descartados + descartadosAlNacer, reversiones);
    }

    /// <summary>Los SOURCE_ID, de entre los pedidos, que otra conciliación de la empresa ya canceló con una fila <c>WITHDRAWAL_CANCEL</c>. Las filas de la
    /// conciliación en curso no cuentan: su cancelación se resuelve por la exclusión del mismo archivo.</summary>
    private static async Task<HashSet<string>> CancelacionesYaImportadasAsync(
        IWaysDbContext db, int idEmpresa, int idConciliacion, IReadOnlyList<string> idsDeOperacion, CancellationToken ct) =>
        (await db.FilasConciliacionMercadoPago
                .AsNoTracking()
                .Where(f => f.IdEmpresa == idEmpresa && f.IdConciliacion != idConciliacion
                            && f.TipoTransaccion == TiposDeTransaccionMercadoPago.WithdrawalCancel
                            && f.SourceId != null && idsDeOperacion.Contains(f.SourceId))
                .Select(f => f.SourceId!)
                .Distinct()
                .ToListAsync(ct))
        .ToHashSet();

    /// <summary>Aplica las cancelaciones del reporte a las propuestas de retiro de la empresa. Las filas de las propuestas se bloquean (<c>FOR UPDATE</c>, por
    /// id ascendente, el mismo lock con el que aplican, descartan y reabren) y recién después se leen: el estado sobre el que se decide es el leído bajo el lock,
    /// así que una aplicación simultánea o ya terminada decide entre descartar y proponer la reversión.</summary>
    private static async Task<(int Descartados, int Reversiones)> ProcesarCancelacionesAsync(
        IWaysDbContext db, int idTenant, int idEmpresa, int idConciliacion, IReadOnlyList<FilaConciliacionMercadoPago> cancelaciones,
        int? idUsuario, ServicioDeAuditoria auditoria, DateTimeOffset ahora, CancellationToken ct)
    {
        if (cancelaciones.Count == 0)
        {
            return (0, 0);
        }

        var claves = cancelaciones.Select(f => PropuestaDeTesoreriaMercadoPago.ClaveDeRetiro(f.SourceId!)).ToList();
        var ids = await db.PropuestasTesoreriaMercadoPago
            .AsNoTracking()
            .Where(p => p.IdEmpresa == idEmpresa && p.Tipo == TiposDePropuestaDeTesoreriaMercadoPago.Retiro && claves.Contains(p.ClaveOrigen))
            .Select(p => p.Id)
            .OrderBy(id => id)
            .ToListAsync(ct);
        foreach (var id in ids)
        {
            await db.Database.ExecuteSqlInterpolatedAsync(
                $"SELECT 1 FROM propuestas_tesoreria_mercadopago WHERE id_propuesta_tesoreria_mercadopago = {id} FOR UPDATE", ct);
        }

        var retiros = await db.PropuestasTesoreriaMercadoPago.Where(p => ids.Contains(p.Id)).ToListAsync(ct);
        var yaRevertidas = (await db.PropuestasTesoreriaMercadoPago
                .AsNoTracking()
                .Where(p => p.IdEmpresa == idEmpresa && p.Tipo == TiposDePropuestaDeTesoreriaMercadoPago.RetiroCancelado && claves.Contains(p.ClaveOrigen))
                .Select(p => p.ClaveOrigen)
                .ToListAsync(ct))
            .ToHashSet();

        var descartados = 0;
        var reversiones = 0;
        foreach (var fila in cancelaciones)
        {
            var clave = PropuestaDeTesoreriaMercadoPago.ClaveDeRetiro(fila.SourceId!);
            var retiro = retiros.FirstOrDefault(p => p.ClaveOrigen == clave);
            if (retiro is null)
            {
                continue;
            }

            if (retiro.Estado == EstadosDePropuestaDeTesoreriaMercadoPago.Pendiente)
            {
                descartados++;
                retiro.Estado = EstadosDePropuestaDeTesoreriaMercadoPago.Descartada;
                retiro.IdUsuarioResolucion = idUsuario;
                retiro.FechaResolucion = ahora;
                retiro.MotivoResolucion = PropuestaDeTesoreriaMercadoPago.MotivoCancelado;
                retiro.CanceladoPorMercadoPago = true;
                retiro.UpdatedAt = ahora;
                auditoria.Registrar(new RegistroDeAuditoria(
                    idTenant,
                    null,
                    AccionAuditada.PropuestaDeTesoreriaDescarte,
                    retiro.Id,
                    valorAnterior: null,
                    valorNuevo: new Dictionary<string, object?>
                    {
                        ["id_conciliacion"] = retiro.IdConciliacion,
                        ["id_conciliacion_cancelacion"] = idConciliacion,
                        ["tipo"] = retiro.Tipo,
                        ["importe"] = retiro.Importe,
                        ["estado"] = retiro.Estado,
                        ["motivo"] = retiro.MotivoResolucion
                    }));
            }
            else if (retiro.Estado == EstadosDePropuestaDeTesoreriaMercadoPago.Aplicada)
            {
                retiro.CanceladoPorMercadoPago = true;
                retiro.UpdatedAt = ahora;
                if (!yaRevertidas.Add(clave))
                {
                    continue;
                }

                reversiones++;
                db.PropuestasTesoreriaMercadoPago.Add(new PropuestaDeTesoreriaMercadoPago
                {
                    IdTenant = idTenant,
                    IdEmpresa = idEmpresa,
                    IdConciliacion = idConciliacion,
                    Tipo = TiposDePropuestaDeTesoreriaMercadoPago.RetiroCancelado,
                    ClaveOrigen = clave,
                    Importe = retiro.Importe,
                    IdFila = fila.Id,
                    IdCuentaOrigen = retiro.IdCuentaOrigen,
                    IdCuentaDestino = retiro.IdCuentaDestino,
                    Estado = EstadosDePropuestaDeTesoreriaMercadoPago.Pendiente,
                    CanceladoPorMercadoPago = true,
                    CreatedAt = ahora,
                    UpdatedAt = ahora
                });
            }
            else if (!retiro.CanceladoPorMercadoPago)
            {
                retiro.CanceladoPorMercadoPago = true;
                retiro.UpdatedAt = ahora;
            }
        }

        await db.SaveChangesAsync(ct);
        return (descartados, reversiones);
    }
}
