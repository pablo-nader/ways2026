using System.Data;
using System.Data.Common;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Storage;
using Ways.Application.Abstracciones;
using Ways.Application.Auditoria;
using Ways.Application.Caja;
using Ways.Application.Fiscal;
using Ways.Application.Parametros;
using Ways.Domain.Auditoria;
using Ways.Domain.Catalogos;
using Ways.Domain.Common;
using Ways.Domain.Fiscal;
using Ways.Domain.MercadoPago;
using Ways.Domain.Ventas;

namespace Ways.Application.Ventas;

/// <summary>
/// Cambia los medios de pago de una venta ya emitida sin cambiar cuánto se cobró: desde la caja mientras el turno de la venta
/// sigue abierto, o desde administración con el turno abierto o cerrado. Los cobros anteriores se dan de baja lógica y se
/// insertan los nuevos (ninguna fila de cuenta corriente ni de aplicaciones los referencia, porque la cuenta corriente queda
/// afuera). Lo demás que dependía de los medios se mueve en la misma transacción: el acumulado de facturación
/// (<see cref="ServicioDeDecisionDeFacturacion.MoverAportesPorCambioDeMediosAsync"/>) y, con el turno cerrado, el arqueo
/// (<see cref="RecalculoDeArqueoDeTurnoCerrado"/>, con la marca del recálculo) y las cuentas de tesorería
/// (<see cref="EscriturasDeCuentaDeTesoreria.CompensarCambioDeMediosAsync"/>).
///
/// Sin reintento (<see cref="FabricaDeEstrategiaSinReintento"/>): da de baja e inserta cobros, mueve el acumulado y escribe
/// ajustes de cuenta y auditoría sin clave de idempotencia; un reintento sobre un commit ambiguo los repetiría.
/// </summary>
public class ServicioDeCambioDeMedios(
    IWaysDbContext db, IRelojDelSistema reloj, IContextoDeUsuario contexto, ServicioDeParametros parametros,
    LectorDeMovimientosDelTurno lectorDeMovimientos, ServicioDeAuditoria auditoria,
    ServicioDeDecisionDeFacturacion decisionDeFacturacion, ServicioDeVentas servicioDeVentas)
{
    /// <summary>Desde la caja: solo mientras el turno de la venta siga abierto, decidido bajo el lock del turno (una venta sin
    /// turno, o con el turno ya cerrado, es <c>409 venta_turno_cerrado</c>).</summary>
    public async Task<ComprobanteEmitido> CambiarDesdeLaCajaAsync(
        int idVenta, SolicitudDeCambioDeMedios solicitud, CancellationToken ct = default)
    {
        var idTenant = ExigirTenantDeLaSesion();
        var inmutables = await LeerDatosInmutablesAsync(idVenta, ct);
        var pagos = ExigirPagos(solicitud);
        if (inmutables.IdTurnoCaja is null)
        {
            throw ReglaDeCambioDeMedios.VentaTurnoCerrado;
        }

        return await EjecutarAsync(idTenant, idVenta, inmutables, pagos, Modo.Caja, ct);
    }

    /// <summary>Desde administración: con el turno abierto, cerrado (recalcula el arqueo y compensa las cuentas) o sin turno.</summary>
    public async Task<ComprobanteEmitido> CambiarDeAdministracionAsync(
        int idVenta, SolicitudDeCambioDeMedios solicitud, CancellationToken ct = default)
    {
        var idTenant = ExigirTenantDeLaSesion();
        var inmutables = await LeerDatosInmutablesAsync(idVenta, ct);
        var pagos = ExigirPagos(solicitud);

        return await EjecutarAsync(idTenant, idVenta, inmutables, pagos, Modo.Administracion, ct);
    }

    private enum Modo
    {
        Caja,
        Administracion
    }

    /// <summary>Columnas que ninguna escritura cambia después de la emisión, leídas antes de los locks (single-read-under-lock
    /// regla 6): la empresa es la clave del primer lock y el turno la del segundo.</summary>
    private sealed record DatosInmutablesDeVenta(int? IdTurnoCaja, int IdPuntoVenta, int IdEmpresa);

    private async Task<DatosInmutablesDeVenta> LeerDatosInmutablesAsync(int idVenta, CancellationToken ct) =>
        await (
            from c in db.ComprobantesVenta.SinEspejosFiscales()
            join pv in db.PuntosVenta.IgnoreQueryFilters(["BajaLogica"]) on c.IdPuntoVenta equals pv.Id
            where c.Id == idVenta
            select new DatosInmutablesDeVenta(c.IdTurnoCaja, c.IdPuntoVenta, pv.IdEmpresa))
            .FirstOrDefaultAsync(ct)
        // ADR-8: mismo 404 para "no existe" y "es de otro tenant"; un espejo fiscal no es una venta y tampoco existe acá.
        ?? throw NoExiste(idVenta);

    private static IReadOnlyList<PagoDeVenta> ExigirPagos(SolicitudDeCambioDeMedios solicitud)
    {
        if (solicitud.Pagos is not { Count: > 0 } pagos || pagos.Any(p => p is null))
        {
            throw new ErrorDominio("pago_no_ingresado", "Tenés que indicar al menos un medio de pago.", 400);
        }

        return pagos;
    }

    private async Task<ComprobanteEmitido> EjecutarAsync(
        int idTenant, int idVenta, DatosInmutablesDeVenta inmutables, IReadOnlyList<PagoDeVenta> pagos, Modo modo,
        CancellationToken ct)
    {
        var idEmpleado = contexto.UsuarioId;
        var momento = reloj.Ahora;
        var tolerancia = await ResolverToleranciaAsync(inmutables, ct);

        var estrategia = FabricaDeEstrategiaSinReintento.CrearEstrategiaSinReintento(db);
        await estrategia.ExecuteAsync(async () =>
            await EscribirAsync(idTenant, idVenta, inmutables, pagos, modo, tolerancia, idEmpleado, momento, ct));

        db.ChangeTracker.Clear();
        return await servicioDeVentas.ObtenerAsync(idVenta, ct);
    }

    /// <summary>
    /// Orden de locks:
    /// 1) compartido de conciliación de la empresa (<see cref="GuardaDeConciliacion"/>, siempre el primero) →
    /// 2) turno de la venta, si tiene: caja <c>FOR SHARE</c> con el estado leído bajo el lock (excluye al cierre sin serializar
    ///    entre sí los cambios del mismo turno); administración <c>FOR UPDATE</c>, que además serializa los recálculos del arqueo
    ///    de un turno cerrado →
    /// 3) la venta <c>FOR UPDATE</c>, y bajo ese lock la ÚNICA lectura de sus cobros; después la decisión de facturación y las
    ///    filas del acumulado por medio ascendente (el orden de <see cref="ServicioDeDecisionDeFacturacion"/>) →
    /// 4) con el turno cerrado: filas del arqueo (cubiertas por el lock 2), advisory de tesorería de la empresa, medios
    ///    <c>FOR SHARE</c> y cuentas <c>FOR NO KEY UPDATE</c> (<see cref="EscriturasDeCuentaDeTesoreria"/>).
    ///
    /// Análisis de ciclo: el cierre toma conciliación → turno (exclusivo) → advisory → medios → cuentas, el mismo orden relativo,
    /// y nunca lockea ventas. La anulación toma el turno <c>FOR SHARE</c> y después la venta, el mismo orden. La decisión, la
    /// emisión del espejo y su cancelación toman la venta antes que la decisión y el acumulado y nunca un turno ni el advisory.
    /// La creación del espejo toma la serie fiscal antes que la venta, pero acá no se toma ninguna serie.
    /// </summary>
    private async Task EscribirAsync(
        int idTenant, int idVenta, DatosInmutablesDeVenta inmutables, IReadOnlyList<PagoDeVenta> pagos, Modo modo,
        decimal tolerancia, int idEmpleado, DateTimeOffset momento, CancellationToken ct)
    {
        await using var transaccion = await db.Database.BeginTransactionAsync(ct);
        var conexion = db.Database.GetDbConnection();
        var transaccionCruda = transaccion.GetDbTransaction();

        await GuardaDeConciliacion.TomarCompartidoAsync(db, idTenant, inmutables.IdEmpresa, ct);

        TurnoBloqueado? turnoCerrado = null;
        if (inmutables.IdTurnoCaja is { } idTurnoCaja)
        {
            var turno = await BloquearTurnoAsync(conexion, transaccionCruda, idTurnoCaja, idTenant, modo, ct);
            if (!turno.Abierto)
            {
                if (modo == Modo.Caja)
                {
                    throw ReglaDeCambioDeMedios.VentaTurnoCerrado;
                }

                turnoCerrado = turno;
            }
        }

        var venta = await BloquearVentaAsync(conexion, transaccionCruda, idVenta, idTenant, ct) ?? throw NoExiste(idVenta);
        if (venta.Estado != "emitido")
        {
            throw ReglaDeCambioDeMedios.VentaAnulada;
        }

        ReglaDeCambioDeMedios.ExigirComprobanteAdmitido(venta.CodigoTipo, venta.Signo);
        ReglaDeCambioDeMedios.ExigirSinFactura(
            venta.ResultadoFiscal, await LeerResultadoDeLaFacturaAsync(conexion, transaccionCruda, idVenta, ct));

        // Los cobros de un turno cerrado cuentan para el libro en la fecha del cierre; la venta, en la suya. La menor decide.
        var fechaQueCuentaParaElLibro = ReglaDeCambioDeMedios.FechaQueCuentaParaElLibro(venta.Fecha, turnoCerrado?.FechaCierre);
        await GuardaDeConciliacion.ExigirNoConciliadoAsync(db, idTenant, inmutables.IdEmpresa, fechaQueCuentaParaElLibro, ct);

        var cobrosVivos = await db.Cobros
            .Where(c => c.IdComprobanteVenta == idVenta)
            .OrderBy(c => c.Id)
            .ToListAsync(ct);

        // E3-T2: el dinero de un pago cobrado con Mercado Pago entró por ese medio y lo respalda un cobro ligado a la venta; cambiarlo
        // desacoplaría ambos (la devolución es de la conciliación). Lo mismo un pago aprobado a mano sin verificar (E5): su dinero todavía no se cotejó
        // y cambiarle el medio borraría la marca que la conciliación necesita. Decidido sobre la única lectura de los cobros, bajo el lock de la venta.
        if (cobrosVivos.Any(c => c.IdCobroMercadoPago is not null || VerificacionDePagoMercadoPago.NacioDeAprobacionManual(c.VerificacionMercadoPago)))
        {
            throw ErrorDominio.Conflicto(
                CodigosDePagoConMercadoPago.NoEditable, "Un pago cobrado o aprobado a mano con Mercado Pago no se puede cambiar.");
        }

        var medios = await LeerMediosAsync(cobrosVivos.Select(c => c.IdMedioPago), pagos.Select(p => p.IdMedioPago), ct);

        // Tampoco se agrega un medio de Mercado Pago: su pago necesita un cobro aprobado, que este camino no tiene número preasignado
        // con el cual pedir.
        foreach (var pago in pagos)
        {
            ReglaDeCobroEnVenta.ExigirFormaDelPago(
                medios[pago.IdMedioPago].Proveedor, pago.IdCobroMercadoPago, pago.Referencia, traeNumeroPreasignado: false,
                aprobacionManualSinVerificar: pago.AprobacionManualSinVerificar);
        }

        var anteriores = cobrosVivos
            .Select(c => new CobroDeVenta(c.IdMedioPago, medios[c.IdMedioPago].Comportamiento, c.Importe, c.Vuelto, c.Referencia))
            .ToList();
        var nuevos = pagos
            .Select(p => new CobroDeVenta(
                p.IdMedioPago, medios[p.IdMedioPago].Comportamiento, p.Importe, p.Vuelto, NormalizarReferencia(p.Referencia)))
            .ToList();

        ReglaDeCambioDeMedios.ExigirSinCuentaCorriente(anteriores, nuevos);

        // Las reglas 5 y 6 (cuenta corriente y límite) no aplican: la cuenta corriente ya quedó afuera arriba.
        ValidadorDePagos.Validar(
            venta.Total,
            nuevos.Select(n => new PagoAValidar(
                n.IdMedioPago, n.Comportamiento, medios[n.IdMedioPago].AdmiteVuelto, medios[n.IdMedioPago].RequiereReferencia,
                n.Importe, n.Vuelto, n.Referencia)).ToList(),
            tolerancia, esConsumidorFinal: false, saldoCliente: 0m, limiteCredito: 0m, creditoIlimitado: true,
            exigirLimiteDeCredito: false);
        ReglaDeCambioDeMedios.ExigirMismoTotalYAlgunCambio(anteriores, nuevos);

        await decisionDeFacturacion.MoverAportesPorCambioDeMediosAsync(
            conexion, transaccionCruda, idVenta, Cobrado(anteriores), Cobrado(nuevos), ct);

        foreach (var cobro in cobrosVivos)
        {
            cobro.DeletedAt = momento;
            cobro.UpdatedAt = momento;
        }

        db.Cobros.AddRange(nuevos.Select(n => new Cobro
        {
            IdTenant = idTenant,
            IdComprobanteVenta = idVenta,
            IdMedioPago = n.IdMedioPago,
            Importe = n.Importe,
            Referencia = n.Referencia,
            Vuelto = n.Vuelto,
            CreatedAt = momento,
            UpdatedAt = momento
        }));
        await db.SaveChangesAsync(ct);

        int? idTurnoRecalculado = null;
        if (turnoCerrado is not null)
        {
            await RecalculoDeArqueoDeTurnoCerrado.RecalcularAsync(
                db, lectorDeMovimientos, idTenant, turnoCerrado.Id, turnoCerrado.IdMedioPagoEfectivo, idEmpleado, momento, ct);
            idTurnoRecalculado = turnoCerrado.Id;

            await EscriturasDeTesoreria.TomarLockDeEmpresaAsync(conexion, transaccionCruda, idTenant, inmutables.IdEmpresa, ct);
            await EscriturasDeCuentaDeTesoreria.CompensarCambioDeMediosAsync(
                db, conexion, transaccionCruda, idTenant, inmutables.IdEmpresa, turnoCerrado.Id, idVenta, momento, idEmpleado,
                ReglaDeCambioDeMedios.VariacionDeBrutoPorMedio(anteriores, nuevos), ct);
        }

        var (valorAnterior, valorNuevo) = PayloadDeAuditoria.CambioDeMediosDeVenta(anteriores, nuevos, idTurnoRecalculado);
        auditoria.Registrar(new RegistroDeAuditoria(
            idTenant, inmutables.IdPuntoVenta, AccionAuditada.VentaCambioDeMedios, idVenta, valorAnterior, valorNuevo));
        await db.SaveChangesAsync(ct);

        await transaccion.CommitAsync(ct);
    }

    private static List<(int IdMedioPago, decimal Monto)> Cobrado(IEnumerable<CobroDeVenta> cobros) =>
        [.. cobros.Select(c => (c.IdMedioPago, c.Importe - c.Vuelto))];

    private static string? NormalizarReferencia(string? referencia) =>
        string.IsNullOrWhiteSpace(referencia) ? null : referencia.Trim();

    private sealed record MedioDelCambio(
        ComportamientoMedioPago Comportamiento, bool AdmiteVuelto, bool RequiereReferencia, string? Proveedor);

    /// <summary>Los medios nuevos tienen que existir y estar vivos (un medio desconocido es <c>400 referencia_invalida</c>, como
    /// en la venta); los que la venta ya tenía se leen aunque estén dados de baja.</summary>
    private async Task<Dictionary<int, MedioDelCambio>> LeerMediosAsync(
        IEnumerable<int> idsAnteriores, IEnumerable<int> idsNuevos, CancellationToken ct)
    {
        var nuevos = idsNuevos.Distinct().ToList();
        var anteriores = idsAnteriores.Distinct().ToList();

        var vivos = await db.MediosPago
            .Where(m => nuevos.Contains(m.Id))
            .Select(m => new { m.Id, m.Comportamiento, m.AdmiteVuelto, m.RequiereReferencia, m.Proveedor })
            .ToDictionaryAsync(m => m.Id, m => new MedioDelCambio(m.Comportamiento, m.AdmiteVuelto, m.RequiereReferencia, m.Proveedor), ct);

        var faltante = nuevos.Except(vivos.Keys).ToList();
        if (faltante.Count > 0)
        {
            throw new ErrorDominio("referencia_invalida", $"No existe el medio de pago {faltante[0]}.", 400);
        }

        var faltantesAnteriores = anteriores.Except(vivos.Keys).ToList();
        if (faltantesAnteriores.Count > 0)
        {
            var dadosDeBaja = await db.MediosPago.IgnoreQueryFilters(["BajaLogica"])
                .Where(m => faltantesAnteriores.Contains(m.Id))
                .Select(m => new { m.Id, m.Comportamiento, m.AdmiteVuelto, m.RequiereReferencia, m.Proveedor })
                .ToListAsync(ct);
            foreach (var medio in dadosDeBaja)
            {
                vivos[medio.Id] = new MedioDelCambio(medio.Comportamiento, medio.AdmiteVuelto, medio.RequiereReferencia, medio.Proveedor);
            }
        }

        return vivos;
    }

    private async Task<decimal> ResolverToleranciaAsync(DatosInmutablesDeVenta inmutables, CancellationToken ct)
    {
        var resuelto = await parametros.ResolverAsync(
            ParametroConocido.ToleranciaPago.Clave, inmutables.IdEmpresa, inmutables.IdPuntoVenta, ct);
        return JsonSerializer.Deserialize<decimal>(resuelto.Valor);
    }

    private sealed record TurnoBloqueado(int Id, bool Abierto, int? IdMedioPagoEfectivo, DateTimeOffset? FechaCierre);

    private static async Task<TurnoBloqueado> BloquearTurnoAsync(
        DbConnection conexion, DbTransaction transaccion, int idTurnoCaja, int idTenant, Modo modo, CancellationToken ct)
    {
        await using var comando = conexion.CreateCommand();
        comando.Transaction = transaccion;
        comando.CommandText =
            "SELECT estado::text, id_medio_pago_efectivo, fecha_cierre FROM turnos_caja " +
            "WHERE id_turno_caja = $1 AND id_tenant = $2 " +
            (modo == Modo.Caja ? "FOR SHARE" : "FOR UPDATE");
        ParametrosDeComando.Agregar(comando, idTurnoCaja);
        ParametrosDeComando.Agregar(comando, idTenant);

        await using var lector = await comando.ExecuteReaderAsync(ct);
        if (!await lector.ReadAsync(ct))
        {
            throw new InvalidOperationException(
                $"El turno {idTurnoCaja} de una venta existente no es visible — invariante de FK violado.");
        }

        return new TurnoBloqueado(
            idTurnoCaja, lector.GetString(0) == "abierto", lector.IsDBNull(1) ? null : lector.GetInt32(1),
            lector.IsDBNull(2) ? null : lector.GetFieldValue<DateTimeOffset>(2));
    }

    private sealed record VentaBloqueada(
        string Estado, ResultadoFiscal? ResultadoFiscal, DateTimeOffset Fecha, decimal Total, string CodigoTipo, short Signo);

    private static async Task<VentaBloqueada?> BloquearVentaAsync(
        DbConnection conexion, DbTransaction transaccion, int idVenta, int idTenant, CancellationToken ct)
    {
        await using var comando = conexion.CreateCommand();
        comando.Transaction = transaccion;
        comando.CommandText =
            "SELECT c.estado::text, c.resultado_fiscal::text, c.fecha, c.total, t.codigo, t.signo " +
            "FROM comprobantes_venta c JOIN tipos_comprobante t ON t.id_tipo_comprobante = c.id_tipo_comprobante " +
            "WHERE c.id_comprobante_venta = $1 AND c.id_tenant = $2 AND c.id_venta_origen IS NULL FOR UPDATE OF c";
        ParametrosDeComando.Agregar(comando, idVenta);
        ParametrosDeComando.Agregar(comando, idTenant);

        await using var lector = await comando.ExecuteReaderAsync(ct);
        if (!await lector.ReadAsync(ct))
        {
            return null;
        }

        return new VentaBloqueada(
            lector.GetString(0), lector.IsDBNull(1) ? null : LeerResultado(lector.GetString(1)),
            lector.GetFieldValue<DateTimeOffset>(2), lector.GetDecimal(3), lector.GetString(4), lector.GetInt16(5));
    }

    /// <summary>El resultado del espejo de la factura de la venta (el de una nota de crédito no cuenta), o <c>null</c> si no tiene.
    /// Leído con la venta bloqueada: un espejo nuevo no puede nacer, porque crearlo bloquea la venta.</summary>
    private static async Task<ResultadoFiscal?> LeerResultadoDeLaFacturaAsync(
        DbConnection conexion, DbTransaction transaccion, int idVenta, CancellationToken ct)
    {
        await using var comando = conexion.CreateCommand();
        comando.Transaction = transaccion;
        comando.CommandText =
            "SELECT resultado_fiscal::text FROM comprobantes_venta " +
            "WHERE id_venta_origen = $1 AND id_comprobante_asociado IS NULL";
        ParametrosDeComando.Agregar(comando, idVenta);

        return await comando.ExecuteScalarAsync(ct) is string resultado ? LeerResultado(resultado) : null;
    }

    private static ResultadoFiscal LeerResultado(string valor) => valor switch
    {
        "pendiente" => ResultadoFiscal.Pendiente,
        "aprobado" => ResultadoFiscal.Aprobado,
        "aprobado_con_observaciones" => ResultadoFiscal.AprobadoConObservaciones,
        "rechazado" => ResultadoFiscal.Rechazado,
        _ => throw new InvalidOperationException($"Resultado fiscal desconocido: {valor}.")
    };

    private static ErrorDominio NoExiste(int idVenta) => ErrorDominio.NoEncontrado($"No existe la venta {idVenta}.");

    private int ExigirTenantDeLaSesion() =>
        contexto.IdTenant
            ?? throw new InvalidOperationException(
                "ServicioDeCambioDeMedios requiere un actor de tenant; la política de la ruta no admite plataforma.");
}
