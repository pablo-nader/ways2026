using System.Data.Common;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Storage;
using Microsoft.Extensions.Logging;
using Ways.Application.Abstracciones;
using Ways.Domain.Catalogos;
using Ways.Domain.Common;
using Ways.Domain.Fiscal;
using Ways.Domain.Organizacion;
using Ways.Domain.Ventas;

namespace Ways.Application.Fiscal;

/// <summary>
/// Emite la factura fiscal espejo de UNA venta cuya decisión de facturar ya está tomada (<c>facturaciones_de_venta</c> en
/// <c>pendiente</c>, ver <see cref="ServicioDeDecisionDeFacturacion"/>). El ticket (la venta) no se toca nunca: el espejo es
/// un comprobante fiscal enlazado por <c>id_venta_origen</c> que lleva comprobante e ítems y nada de stock, pagos, cuenta
/// corriente ni turno.
///
/// Dos transacciones, porque ARCA puede autorizar un número aunque nunca recibamos la respuesta, así que el número tiene que
/// sobrevivir a la falta de respuesta (la emisión directa, <see cref="ServicioDeFacturacionFiscal"/>, lo deshace todo ante
/// ese caso):
///   A) toma el lock de la serie, verifica que la serie no esté detenida por otro espejo pendiente, bloquea la venta y
///      verifica que siga emitida, toma el número e inserta el espejo en <c>pendiente</c>. Commit.
///   B) retoma el lock de la serie (el que sostiene la llamada a ARCA) y pide el CAE; si el espejo ya existía de una
///      llamada anterior, consulta primero y solo pide un CAE nuevo si ARCA no tiene el número. Aplica el resultado y
///      actualiza la decisión, guardada por <c>estado = 'pendiente'</c>.
/// Las dos usan la estrategia sin reintento: un reintento tras un commit ambiguo quemaría otro número.
///
/// Orden de locks: <c>numeraciones_fiscales</c> → <c>comprobantes_venta</c> → <c>facturaciones_de_venta</c> →
/// <c>acumulados_de_facturacion</c> (por medio de pago ascendente). El acumulado solo se escribe con SQL crudo, igual que
/// en <see cref="ServicioDeDecisionDeFacturacion"/>, y cada reversión es la contraparte exacta de lo que aplicó esa decisión.
///
/// Un resultado es definitivo solo si está en la lista blanca: una <see cref="RespuestaCae"/> (aprobado, aprobado con
/// observaciones o rechazado) o el error <c>arca_rechazo</c>. Todo lo demás (transporte, falla SOAP, ticket de acceso,
/// numeración desincronizada, lecturas) deja el espejo pendiente con su número.
///
/// La espera entre intentos (<c>proximo_intento_en</c>) solo se REGISTRA: nada la lee todavía. La respetará el procesador por
/// lote; este servicio procesa la decisión que se le pide aunque su espera no haya vencido.
/// </summary>
public class ServicioDeFacturacionDeVentas(
    IWaysDbContext db,
    IRelojDelSistema reloj,
    IContextoDeUsuario contexto,
    ServicioDeFacturacionFiscal fiscal,
    IClienteWsfe clienteWsfe,
    ILogger<ServicioDeFacturacionDeVentas> log)
{
    public static readonly TimeSpan EsperaInicialNoDefinitiva = TimeSpan.FromSeconds(30);

    public static readonly TimeSpan EsperaMaximaNoDefinitiva = TimeSpan.FromMinutes(10);

    public static readonly TimeSpan EsperaInicialDeCompuerta = TimeSpan.FromMinutes(5);

    public static readonly TimeSpan EsperaMaximaDeCompuerta = TimeSpan.FromHours(4);

    public const string SerieOcupada = "serie_ocupada";

    public const string SerieBloqueada = "serie_bloqueada";

    private const string Pendiente = "pendiente";

    private const string Emitida = "emitida";

    private const string Rechazada = "rechazada";

    private const string Cancelada = "cancelada";

    private static readonly JsonSerializerOptions OpcionesDeAportes = new(JsonSerializerDefaults.Web);

    // ---- API -----------------------------------------------------------------------------------

    /// <summary>Procesa la decisión: devuelve su estado resultante. Idempotente: una decisión que ya no está pendiente
    /// devuelve su estado sin ninguna llamada a ARCA. Una decisión inexistente o de otro tenant es un 404.</summary>
    public async Task<EstadoFiscalDeVenta> ProcesarDecisionAsync(int idFacturacion, CancellationToken ct = default)
    {
        var idTenant = contexto.IdTenant
            ?? throw new InvalidOperationException(
                "ServicioDeFacturacionDeVentas requiere un actor de tenant; OperacionDePos no admite plataforma.");
        db.ChangeTracker.Clear();

        var decision = await LeerDecisionAsync(idFacturacion, ct)
            ?? throw ErrorDominio.NoEncontrado($"No existe la facturación {idFacturacion}.");
        if (decision.Estado != Pendiente)
        {
            return await ProyectarAsync(decision, null, ct);
        }

        var venta = await db.ComprobantesVenta.AsNoTracking()
            .FirstAsync(c => c.Id == decision.IdComprobanteVenta, ct);
        var espejo = await db.ComprobantesVenta.AsNoTracking()
            .FirstOrDefaultAsync(c => c.IdVentaOrigen == decision.IdComprobanteVenta, ct);

        // Una venta anulada es terminal (emitido → anulado) y un espejo no se crea sobre ella, así que esta lectura sin lock
        // no puede quedar vieja: lo que decide la anulación contra el espejo se decide bajo el lock de la venta.
        if (espejo is null && venta.Estado != EstadoComprobante.Emitido)
        {
            return await CancelarAsync(decision, ct);
        }

        var origen = espejo ?? venta;
        var evaluacion = await EvaluarCompuertasAsync(origen, espejo is not null, ct);
        if (evaluacion.Fallo is { } fallo)
        {
            return await RegistrarFalloAsync(
                decision, $"{fallo.Codigo}: {fallo.Mensaje}", EsperaInicialDeCompuerta, EsperaMaximaDeCompuerta, ct);
        }

        var plan = evaluacion.Plan!;

        TicketDeAcceso ticket;
        try
        {
            ticket = await fiscal.ObtenerTicketAsync(plan.Empresa, plan.Ambiente, ct);
        }
        catch (Exception ex) when (!ct.IsCancellationRequested)
        {
            return await RegistrarFalloAsync(
                decision, DescribirFalla(ex), EsperaInicialNoDefinitiva, EsperaMaximaNoDefinitiva, ct);
        }

        EspejoEnTramite enTramite;
        if (espejo is null)
        {
            var creacion = await CrearEspejoAsync(venta, plan, idTenant, ct);
            switch (creacion.Resultado)
            {
                case ResultadoDeCreacion.VentaNoEmitida:
                    return await CancelarAsync(decision, ct);
                case ResultadoDeCreacion.SerieOcupada:
                    return await ProyectarAsync(decision, $"{SerieOcupada}: otra llamada está usando la serie.", ct);
                case ResultadoDeCreacion.SerieBloqueada:
                    return await ProyectarAsync(
                        decision, $"{SerieBloqueada}: la serie espera que otro comprobante pendiente se resuelva.", ct);
            }

            enTramite = creacion.Espejo!;
        }
        else
        {
            enTramite = new EspejoEnTramite(espejo.Id, espejo.Numero, espejo.Fecha, Nuevo: false);
        }

        return await ResolverEspejoAsync(decision, plan, enTramite, ticket, idTenant, ct);
    }

    // ---- Compuertas (sin red) --------------------------------------------------------------------

    private sealed record FalloDeCompuerta(string Codigo, string Mensaje);

    private sealed record Plan(
        PuntoVenta PuntoVenta, Empresa Empresa, TipoComprobante Tipo, char Letra, string CuitEmisor,
        short CondicionIvaReceptor, short TipoDocReceptor, long NroDocReceptor, TotalesFiscales Totales,
        AmbienteFiscal Ambiente)
    {
        public ClaveDeSerie Serie => new(PuntoVenta.NumeroFiscal!.Value, Tipo.CodigoAfip!.Value);
    }

    private sealed record Evaluacion(Plan? Plan, FalloDeCompuerta? Fallo);

    /// <summary>Las compuertas se evalúan sobre el ticket (si todavía no hay espejo) o sobre el espejo existente, cuyo tipo,
    /// cliente e ítems ya quedaron fijados por la llamada que lo creó.</summary>
    private async Task<Evaluacion> EvaluarCompuertasAsync(ComprobanteVenta origen, bool origenEsEspejo, CancellationToken ct)
    {
        static Evaluacion Falla(string codigo, string mensaje) => new(null, new FalloDeCompuerta(codigo, mensaje));

        var ambiente = fiscal.ResolverAmbiente();
        var puntoVenta = await db.PuntosVenta.AsNoTracking().FirstAsync(p => p.Id == origen.IdPuntoVenta, ct);
        var empresa = await db.Empresas.AsNoTracking().FirstAsync(e => e.Id == puntoVenta.IdEmpresa, ct);

        if (empresa.IdCondicionFiscal is not { } idCondicionEmisor)
        {
            return Falla("empresa_sin_condicion_fiscal", "La empresa no tiene cargada su condición fiscal ARCA.");
        }

        if (puntoVenta.NumeroFiscal is null)
        {
            return Falla("punto_venta_sin_numero_fiscal", "El punto de venta no tiene cargado su número fiscal ARCA.");
        }

        var cliente = await db.Clientes.AsNoTracking().FirstAsync(c => c.Id == origen.IdCliente, ct);
        var condicionEmisor = await db.CondicionesFiscales.AsNoTracking().FirstAsync(c => c.Id == idCondicionEmisor, ct);
        var condicionReceptor = await db.CondicionesFiscales.AsNoTracking().FirstAsync(c => c.Id == cliente.IdCondicionFiscal, ct);

        if (condicionReceptor.Codigo == "NO_RESP" || condicionReceptor.CodigoAfip is null)
        {
            return Falla(
                "condicion_fiscal_receptor_no_mapeada",
                $"La condición fiscal del receptor ('{condicionReceptor.Codigo}') no tiene mapeo ARCA confirmado.");
        }

        var hayCertificadoActivo = await db.CertificadosFiscales.AsNoTracking()
            .AnyAsync(c => c.IdEmpresa == empresa.Id && c.Ambiente == ambiente && c.Activo, ct);
        if (!hayCertificadoActivo)
        {
            return Falla("certificado_fiscal_ausente", "No hay un certificado fiscal activo para esta empresa y ambiente.");
        }

        string cuitEmisor;
        try
        {
            cuitEmisor = ServicioDeFacturacionFiscal.ExigirCuitNumerico(empresa);
        }
        catch (ErrorDominio ex) when (ex.Codigo == "empresa_sin_cuit_valido")
        {
            return Falla(ex.Codigo, ex.Message);
        }

        var letra = ResolvedorDeLetraComprobante.Resolver(
            ServicioDeFacturacionFiscal.MapearCondicionFiscal(condicionEmisor.Codigo),
            ServicioDeFacturacionFiscal.MapearCondicionFiscal(condicionReceptor.Codigo));

        TipoComprobante? tipo;
        if (origenEsEspejo)
        {
            tipo = await db.TiposComprobante.AsNoTracking().FirstAsync(t => t.Id == origen.IdTipoComprobante, ct);
        }
        else
        {
            // La factura de la letra: FA, FB o FC. Las notas de débito comparten letra y signo con la factura, así que la
            // letra sola no alcanza para elegir el tipo.
            var codigoDeFactura = $"F{letra}";
            tipo = await db.TiposComprobante.AsNoTracking()
                .FirstOrDefaultAsync(
                    t => t.Codigo == codigoDeFactura && t.Clase == ClaseComprobante.Venta && t.EsFiscal && t.Activo
                        && t.Letra == letra && t.CodigoAfip != null,
                    ct);
        }

        if (tipo?.CodigoAfip is null)
        {
            return Falla("tipo_fiscal_invalido", $"No hay un tipo de factura fiscal activo para la letra '{letra}'.");
        }

        var (tipoDoc, nroDoc) = ServicioDeFacturacionFiscal.MapearDocumentoArca(cliente);
        const short DocumentoCuit = 80;
        if (letra == 'A' && tipoDoc != DocumentoCuit)
        {
            return Falla("receptor_sin_cuit", "Una factura A exige que el receptor esté identificado con su CUIT.");
        }

        var lineas = await fiscal.ComponerLineasFiscalesDesdeItemsAsync(origen.Id, ct);
        TotalesFiscales totales;
        try
        {
            totales = ComposicionDeTotalesFiscales.Componer(lineas);
        }
        catch (ErrorDominio ex) when (ex.Codigo == "alicuota_sin_mapeo_afip")
        {
            return Falla(ex.Codigo, ex.Message);
        }

        if (totales.ImpTotal != origen.Total)
        {
            return Falla(
                "total_fiscal_no_coincide",
                $"El total fiscal ({totales.ImpTotal}) no coincide con el total del ticket ({origen.Total}).");
        }

        return new Evaluacion(
            new Plan(
                puntoVenta, empresa, tipo, letra, cuitEmisor, condicionReceptor.CodigoAfip.Value, tipoDoc, nroDoc,
                totales, ambiente),
            null);
    }

    // ---- Transacción A: número + espejo pendiente -------------------------------------------------

    private enum ResultadoDeCreacion
    {
        Creado,
        SerieOcupada,
        SerieBloqueada,
        VentaNoEmitida
    }

    private sealed record EspejoEnTramite(int Id, long Numero, DateTimeOffset Fecha, bool Nuevo);

    private sealed record Creacion(ResultadoDeCreacion Resultado, EspejoEnTramite? Espejo);

    private async Task<Creacion> CrearEspejoAsync(
        ComprobanteVenta venta, Plan plan, int idTenant, CancellationToken ct)
    {
        var momento = reloj.Ahora;
        var codigoAfip = plan.Tipo.CodigoAfip!.Value;
        var estrategia = FabricaDeEstrategiaSinReintento.CrearEstrategiaSinReintento(db);

        return await estrategia.ExecuteAsync(async () =>
        {
            await using var transaccion = await db.Database.BeginTransactionAsync(ct);

            await AsignadorDeNumeroFiscal.AsegurarContadorAsync(db, idTenant, plan.PuntoVenta.Id, codigoAfip, ct);
            if (!await TomarLockDeLaSerieAsync(plan.PuntoVenta.Id, codigoAfip, ct))
            {
                return new Creacion(ResultadoDeCreacion.SerieOcupada, null);
            }

            if (await HayEspejoPendienteEnLaSerieAsync(plan.PuntoVenta.Id, codigoAfip, ct))
            {
                return new Creacion(ResultadoDeCreacion.SerieBloqueada, null);
            }

            // El lock de la venta serializa contra la anulación: o la anulación ya comiteó y la venta deja de estar
            // emitida, o este espejo comitea primero y la anulación lo encuentra.
            if (await BloquearVentaYLeerEstadoAsync(venta.Id, ct) != "emitido")
            {
                return new Creacion(ResultadoDeCreacion.VentaNoEmitida, null);
            }

            var numero = await AsignadorDeNumeroFiscal.AsignarSiguienteAsync(db, plan.PuntoVenta.Id, codigoAfip, ct);

            var itemsDeLaVenta = await db.ItemsComprobanteVenta.AsNoTracking()
                .Where(i => i.IdComprobanteVenta == venta.Id)
                .OrderBy(i => i.Orden)
                .ToListAsync(ct);

            var espejo = new ComprobanteVenta
            {
                IdTipoComprobante = plan.Tipo.Id,
                Numero = numero,
                Fecha = momento,
                IdPuntoVenta = plan.PuntoVenta.Id,
                IdTurnoCaja = null,
                IdEmpleado = venta.IdEmpleado,
                IdCliente = venta.IdCliente,
                IdVentaOrigen = venta.Id,
                Subtotal = venta.Subtotal,
                DescuentoTotal = venta.DescuentoTotal,
                DescuentoManualTotal = venta.DescuentoManualTotal,
                RecargoManualTotal = venta.RecargoManualTotal,
                Total = plan.Totales.ImpTotal,
                NetoGravado = plan.Totales.ImpNeto,
                IvaTotal = plan.Totales.ImpIVA,
                Estado = EstadoComprobante.Emitido,
                ResultadoFiscal = ResultadoFiscal.Pendiente,
                FacturacionDecididaEn = null,
                CreatedAt = momento,
                UpdatedAt = momento
            };
            db.ComprobantesVenta.Add(espejo);
            await db.SaveChangesAsync(ct);

            foreach (var item in itemsDeLaVenta)
            {
                db.ItemsComprobanteVenta.Add(new ItemComprobanteVenta
                {
                    IdComprobanteVenta = espejo.Id,
                    Orden = item.Orden,
                    IdArticulo = item.IdArticulo,
                    Descripcion = item.Descripcion,
                    CodigoBarra = item.CodigoBarra,
                    IdArea = item.IdArea,
                    IdListaPrecio = item.IdListaPrecio,
                    IdOferta = item.IdOferta,
                    IdCombo = item.IdCombo,
                    IdAlicuotaIva = item.IdAlicuotaIva,
                    PorcentajeIva = item.PorcentajeIva,
                    Cantidad = item.Cantidad,
                    PrecioUnitario = item.PrecioUnitario,
                    Descuento = item.Descuento,
                    AjusteManualPorcentaje = item.AjusteManualPorcentaje,
                    AjusteManual = item.AjusteManual,
                    Total = item.Total,
                    PorImporte = item.PorImporte,
                    CreatedAt = momento,
                    UpdatedAt = momento
                });
            }

            await db.SaveChangesAsync(ct);
            await transaccion.CommitAsync(ct);
            db.ChangeTracker.Clear();

            return new Creacion(ResultadoDeCreacion.Creado, new EspejoEnTramite(espejo.Id, numero, momento, Nuevo: true));
        });
    }

    /// <summary>Toma el lock de la fila de la serie sin esperar: si otra llamada la tiene (sosteniendo la llamada a ARCA),
    /// no se espera ni se penaliza la decisión.</summary>
    private async Task<bool> TomarLockDeLaSerieAsync(int idPuntoVenta, short codigoAfip, CancellationToken ct)
    {
        await using var comando = await NuevoComandoAsync(
            "SELECT proximo_numero FROM numeraciones_fiscales " +
            "WHERE id_punto_venta = $1 AND codigo_afip = $2 FOR UPDATE SKIP LOCKED",
            ct, idPuntoVenta, codigoAfip);
        return await comando.ExecuteScalarAsync(ct) is not null;
    }

    private async Task<bool> HayEspejoPendienteEnLaSerieAsync(int idPuntoVenta, short codigoAfip, CancellationToken ct)
    {
        await using var comando = await NuevoComandoAsync(
            "SELECT EXISTS (SELECT 1 FROM comprobantes_venta c " +
            "JOIN tipos_comprobante t ON t.id_tipo_comprobante = c.id_tipo_comprobante " +
            "WHERE c.id_punto_venta = $1 AND t.codigo_afip = $2 " +
            "AND c.id_venta_origen IS NOT NULL AND c.resultado_fiscal = 'pendiente')",
            ct, idPuntoVenta, codigoAfip);
        return (bool)(await comando.ExecuteScalarAsync(ct))!;
    }

    private async Task<string?> BloquearVentaYLeerEstadoAsync(int idVenta, CancellationToken ct)
    {
        await using var comando = await NuevoComandoAsync(
            "SELECT estado::text FROM comprobantes_venta " +
            "WHERE id_comprobante_venta = $1 AND id_venta_origen IS NULL FOR UPDATE",
            ct, idVenta);
        return (string?)await comando.ExecuteScalarAsync(ct);
    }

    // ---- Transacción B: CAE, resultado y decisión -------------------------------------------------

    private abstract record ResultadoDeRed;

    private sealed record Definitivo(RespuestaCae Respuesta) : ResultadoDeRed;

    private sealed record NoDefinitivo(string Descripcion) : ResultadoDeRed;

    private async Task<EstadoFiscalDeVenta> ResolverEspejoAsync(
        FacturacionPendiente decision, Plan plan, EspejoEnTramite enTramite, TicketDeAcceso ticket, int idTenant,
        CancellationToken ct)
    {
        var codigoAfip = plan.Tipo.CodigoAfip!.Value;
        var estrategia = FabricaDeEstrategiaSinReintento.CrearEstrategiaSinReintento(db);
        string? errorSinPenalidad = null;
        string? errorDeGuardaDelNumero = null;

        await estrategia.ExecuteAsync(async () =>
        {
            await using var transaccion = await db.Database.BeginTransactionAsync(ct);

            if (!await TomarLockDeLaSerieAsync(plan.PuntoVenta.Id, codigoAfip, ct))
            {
                errorSinPenalidad = $"{SerieOcupada}: otra llamada está usando la serie.";
                return;
            }

            // Bajo el lock de la serie: otra llamada pudo resolver la decisión entre la lectura inicial y este lock.
            if (await LeerEstadoDeLaDecisionAsync(decision.Id, ct) != Pendiente)
            {
                return;
            }

            var (resultadoFiscal, fecha) = await LeerEspejoAsync(enTramite.Id, ct);
            if (resultadoFiscal != Pendiente)
            {
                throw new InvalidOperationException(
                    $"El espejo {enTramite.Id} está '{resultadoFiscal}' pero su decisión {decision.Id} sigue pendiente.");
            }

            var resultado = await PedirCaeAsync(plan, enTramite, ticket, fecha, ct);

            switch (resultado)
            {
                case Definitivo { Respuesta.Resultado: ResultadoFiscal.Rechazado } rechazo:
                    var rechazoAplicado = await ResolverRechazoAsync(decision, plan, enTramite, rechazo.Respuesta, idTenant, ct);
                    if (!rechazoAplicado.Aplicado)
                    {
                        return;
                    }

                    errorDeGuardaDelNumero = rechazoAplicado.ErrorDeGuardaDelNumero;
                    break;

                case Definitivo aprobado:
                    if (await fiscal.AplicarResultadoGuardadoAsync(idTenant, enTramite.Id, aprobado.Respuesta, ct) == 0)
                    {
                        throw new InvalidOperationException(
                            $"La actualización guardada no afectó ninguna fila para el espejo {enTramite.Id}.");
                    }

                    await AsignadorDeNumeroFiscal.ReconciliarAsync(
                        db, plan.PuntoVenta.Id, codigoAfip, enTramite.Numero, reloj, ct);
                    if (await ActualizarDecisionAsync(decision.Id, Emitida, enTramite.Id, null, ct) == 0)
                    {
                        return;
                    }

                    break;

                case NoDefinitivo noDefinitivo:
                    if (await EscribirFalloAsync(
                            decision.Id, noDefinitivo.Descripcion, EsperaInicialNoDefinitiva, EsperaMaximaNoDefinitiva, ct)
                        is null)
                    {
                        return;
                    }

                    break;
            }

            await transaccion.CommitAsync(ct);
        });

        if (errorDeGuardaDelNumero is not null)
        {
            log.LogError(
                "No se pudo devolver el número {Numero} del espejo {IdEspejo} a la serie (punto de venta {IdPuntoVenta}, " +
                "tipo {CodigoAfip}): {Detalle} El espejo queda rechazado con su número y la serie queda desincronizada con ARCA.",
                enTramite.Numero, enTramite.Id, plan.PuntoVenta.Id, codigoAfip, errorDeGuardaDelNumero);
        }

        db.ChangeTracker.Clear();
        var final = await LeerDecisionAsync(decision.Id, ct)
            ?? throw new InvalidOperationException($"La decisión {decision.Id} dejó de existir.");
        return await ProyectarAsync(final, errorSinPenalidad, ct);
    }

    /// <summary>Consulta primero si hay un intento previo (I2); sin él, pide el CAE directo. Devuelve un
    /// <see cref="Definitivo"/> solo para la lista blanca de resultados definitivos (una respuesta de ARCA o el error
    /// <c>arca_rechazo</c>); cualquier otra falla es <see cref="NoDefinitivo"/>.</summary>
    private async Task<ResultadoDeRed> PedirCaeAsync(
        Plan plan, EspejoEnTramite enTramite, TicketDeAcceso ticket, DateTimeOffset fechaDelEspejo, CancellationToken ct)
    {
        var numero = enTramite.Numero;
        var decide = MaquinaDeEstadosCae.Decidir(
            enTramite.Nuevo ? EstadoDeIntento.SinIntentoPrevio : EstadoDeIntento.NoDefinitivo);

        if (decide == DecisionDeReintento.ConsultarPrimero)
        {
            ConsultaDeComprobante consulta;
            try
            {
                consulta = await clienteWsfe.ConsultarAsync(ticket, plan.CuitEmisor, plan.Serie, numero, ct);
            }
            catch (Exception ex) when (!ct.IsCancellationRequested)
            {
                return new NoDefinitivo(DescribirFalla(ex));
            }

            if (consulta.Encontrado)
            {
                return new Definitivo(new RespuestaCae(
                    consulta.Resultado!.Value, consulta.Cae, consulta.CaeVencimiento, [], []));
            }

            // ARCA no tiene el número: se pide con el mismo número y la fecha de hoy.
            fechaDelEspejo = reloj.Ahora;
            await RefrescarFechaDelEspejoAsync(enTramite.Id, fechaDelEspejo, ct);
        }

        var fechaFiscal = await fiscal.ResolverFechaFiscalAsync(plan.Empresa.Id, fechaDelEspejo, ct);
        var totales = plan.Totales;
        var solicitud = new SolicitudDeCae(
            plan.Serie, numero, numero, 1, plan.TipoDocReceptor, plan.NroDocReceptor, fechaFiscal, totales.ImpTotal,
            totales.ImpTotConc, totales.ImpNeto, totales.ImpOpEx, totales.ImpTrib, totales.ImpIVA,
            plan.CondicionIvaReceptor, totales.Iva);

        try
        {
            return new Definitivo(await fiscal.SolicitarCaeConReintentoDeTicketAsync(
                plan.Empresa, plan.Ambiente, plan.CuitEmisor, enTramite.Id, numero, solicitud, ct));
        }
        catch (ErrorDominio ex) when (ex.Codigo == "arca_rechazo")
        {
            return new Definitivo(new RespuestaCae(
                ResultadoFiscal.Rechazado, null, null, [], [new ObservacionArca(0, ex.Message)]));
        }
        catch (Exception ex) when (!ct.IsCancellationRequested)
        {
            return new NoDefinitivo(DescribirFalla(ex));
        }
    }

    private readonly record struct RechazoResuelto(bool Aplicado, string? ErrorDeGuardaDelNumero);

    /// <summary>Rechazo definitivo de ARCA: el espejo se borra y su número vuelve a la serie (regla 1), la decisión pasa a
    /// rechazada con los errores de ARCA y se devuelve el débito del acumulado (regla 3). Si el contador ya avanzó, el número
    /// no se puede devolver y el espejo queda rechazado con su número. Si la decisión ya no estaba pendiente no se aplica nada
    /// (la transacción se deshace).</summary>
    private async Task<RechazoResuelto> ResolverRechazoAsync(
        FacturacionPendiente decision, Plan plan, EspejoEnTramite enTramite, RespuestaCae respuesta, int idTenant,
        CancellationToken ct)
    {
        string? errorDeGuarda = null;

        if (await DevolverNumeroAsync(plan.PuntoVenta.Id, plan.Tipo.CodigoAfip!.Value, enTramite.Numero, ct) == 1)
        {
            await BorrarEspejoAsync(enTramite.Id, ct);
        }
        else
        {
            errorDeGuarda = $"El contador no estaba en {enTramite.Numero + 1}.";
            if (await fiscal.AplicarResultadoGuardadoAsync(idTenant, enTramite.Id, respuesta, ct) == 0)
            {
                throw new InvalidOperationException(
                    $"La actualización guardada no afectó ninguna fila para el espejo {enTramite.Id}.");
            }
        }

        if (await ActualizarDecisionAsync(decision.Id, Rechazada, null, ConstruirTextoDeErrores(respuesta), ct) == 0)
        {
            return new RechazoResuelto(false, null);
        }

        var vendido = await LeerVendidoPorMedioAsync(decision.IdComprobanteVenta, ct);
        foreach (var (idMedio, monto) in vendido.OrderBy(v => v.Key))
        {
            await AplicarAlAcumuladoAsync(decision.IdEmpresa, idMedio, decision.Periodo, monto, 0m, -monto, ct);
        }

        return new RechazoResuelto(true, errorDeGuarda);
    }

    // ---- Cancelación -----------------------------------------------------------------------------

    private async Task<EstadoFiscalDeVenta> CancelarAsync(FacturacionPendiente decision, CancellationToken ct)
    {
        var estrategia = FabricaDeEstrategiaSinReintento.CrearEstrategiaSinReintento(db);

        await estrategia.ExecuteAsync(async () =>
        {
            await using var transaccion = await db.Database.BeginTransactionAsync(ct);

            if (await ActualizarDecisionAsync(decision.Id, Cancelada, null, null, ct) == 0)
            {
                return;
            }

            var aportes = JsonSerializer.Deserialize<List<AporteDeSaldo>>(decision.Aportes, OpcionesDeAportes)!;
            var vendido = await LeerVendidoPorMedioAsync(decision.IdComprobanteVenta, ct);
            foreach (var inverso in PoliticaDeFacturacionPorMedio.Revertir(aportes).OrderBy(a => a.IdMedioPago))
            {
                var monto = vendido.GetValueOrDefault(inverso.IdMedioPago);
                await AplicarAlAcumuladoAsync(
                    decision.IdEmpresa, inverso.IdMedioPago, decision.Periodo, inverso.Aporte, -monto, -monto, ct);
            }

            await transaccion.CommitAsync(ct);
        });

        db.ChangeTracker.Clear();
        var final = await LeerDecisionAsync(decision.Id, ct)
            ?? throw new InvalidOperationException($"La decisión {decision.Id} dejó de existir.");
        return await ProyectarAsync(final, null, ct);
    }

    // ---- Fallos ----------------------------------------------------------------------------------

    private async Task<EstadoFiscalDeVenta> RegistrarFalloAsync(
        FacturacionPendiente decision, string descripcion, TimeSpan espera, TimeSpan tope, CancellationToken ct)
    {
        _ = await EscribirFalloAsync(decision.Id, descripcion, espera, tope, ct);
        var final = await LeerDecisionAsync(decision.Id, ct)
            ?? throw new InvalidOperationException($"La decisión {decision.Id} dejó de existir.");
        return await ProyectarAsync(final, null, ct);
    }

    /// <summary>Suma un intento fallido a la decisión pendiente y registra cuándo volver a intentar:
    /// <c>espera × 2^intentos</c> con tope, contada desde el reloj del servicio. Solo se registra, nadie la lee todavía.
    /// Devuelve la cantidad de intentos, o <c>null</c> si la decisión ya no estaba pendiente.</summary>
    private async Task<int?> EscribirFalloAsync(
        int idFacturacion, string descripcion, TimeSpan espera, TimeSpan tope, CancellationToken ct)
    {
        await using var comando = await NuevoComandoAsync(
            "UPDATE facturaciones_de_venta SET intentos = intentos + 1, ultimo_error = $2, updated_at = $3, " +
            "proximo_intento_en = $3::timestamptz + " +
            "make_interval(secs => LEAST($4::double precision * power(2, LEAST(intentos, 30)), $5::double precision)) " +
            "WHERE id_facturacion_venta = $1 AND estado = 'pendiente'::estado_facturacion_venta " +
            "RETURNING intentos",
            ct, idFacturacion, descripcion, reloj.Ahora, espera.TotalSeconds, tope.TotalSeconds);
        return await comando.ExecuteScalarAsync(ct) as int?;
    }

    private static string DescribirFalla(Exception ex) =>
        ex is ErrorDominio dominio ? $"{dominio.Codigo}: {dominio.Message}" : $"{ex.GetType().Name}: {ex.Message}";

    private static string ConstruirTextoDeErrores(RespuestaCae respuesta) =>
        respuesta.Errors.Count == 0
            ? "ARCA rechazó el comprobante."
            : string.Join("; ", respuesta.Errors.Select(e => $"{e.Codigo}: {e.Mensaje}"));

    // ---- Lecturas y escrituras de la decisión ------------------------------------------------------

    private sealed record FacturacionPendiente(
        int Id, int IdEmpresa, int IdComprobanteVenta, string Estado, string Motivo, string Aportes, DateOnly Periodo,
        int? IdComprobanteFiscal, string? UltimoError);

    private async Task<FacturacionPendiente?> LeerDecisionAsync(int idFacturacion, CancellationToken ct)
    {
        await using var comando = await NuevoComandoAsync(
            "SELECT id_facturacion_venta, id_empresa, id_comprobante_venta, estado::text, motivo::text, aportes::text, " +
            "periodo, id_comprobante_fiscal, ultimo_error FROM facturaciones_de_venta WHERE id_facturacion_venta = $1",
            ct, idFacturacion);
        await using var lector = await comando.ExecuteReaderAsync(ct);
        if (!await lector.ReadAsync(ct))
        {
            return null;
        }

        return new FacturacionPendiente(
            lector.GetInt32(0), lector.GetInt32(1), lector.GetInt32(2), lector.GetString(3), lector.GetString(4),
            lector.GetString(5), lector.GetFieldValue<DateOnly>(6), lector.IsDBNull(7) ? null : lector.GetInt32(7),
            lector.IsDBNull(8) ? null : lector.GetString(8));
    }

    private async Task<string?> LeerEstadoDeLaDecisionAsync(int idFacturacion, CancellationToken ct)
    {
        await using var comando = await NuevoComandoAsync(
            "SELECT estado::text FROM facturaciones_de_venta WHERE id_facturacion_venta = $1", ct, idFacturacion);
        return (string?)await comando.ExecuteScalarAsync(ct);
    }

    /// <summary>Guardada por <c>estado = 'pendiente'</c>: la decisión solo sale de pendiente una vez. Devuelve las filas
    /// afectadas.</summary>
    private async Task<int> ActualizarDecisionAsync(
        int idFacturacion, string estado, int? idComprobanteFiscal, string? error, CancellationToken ct)
    {
        await using var comando = await NuevoComandoAsync(
            "UPDATE facturaciones_de_venta SET estado = $2::estado_facturacion_venta, id_comprobante_fiscal = $3, " +
            "ultimo_error = $4, updated_at = $5 " +
            "WHERE id_facturacion_venta = $1 AND estado = 'pendiente'::estado_facturacion_venta",
            ct, idFacturacion, estado, idComprobanteFiscal, error, reloj.Ahora);
        return await comando.ExecuteNonQueryAsync(ct);
    }

    // ---- El espejo ---------------------------------------------------------------------------------

    private async Task<(string ResultadoFiscal, DateTimeOffset Fecha)> LeerEspejoAsync(int idEspejo, CancellationToken ct)
    {
        await using var comando = await NuevoComandoAsync(
            "SELECT resultado_fiscal::text, fecha FROM comprobantes_venta WHERE id_comprobante_venta = $1", ct, idEspejo);
        await using var lector = await comando.ExecuteReaderAsync(ct);
        if (!await lector.ReadAsync(ct))
        {
            throw new InvalidOperationException($"El espejo {idEspejo} no existe.");
        }

        return (lector.GetString(0), lector.GetFieldValue<DateTimeOffset>(1));
    }

    private async Task RefrescarFechaDelEspejoAsync(int idEspejo, DateTimeOffset fecha, CancellationToken ct)
    {
        await using var comando = await NuevoComandoAsync(
            "UPDATE comprobantes_venta SET fecha = $2, updated_at = $2 " +
            "WHERE id_comprobante_venta = $1 AND resultado_fiscal = 'pendiente'",
            ct, idEspejo, fecha);
        await comando.ExecuteNonQueryAsync(ct);
    }

    /// <summary>Devuelve el número a la serie solo si el contador sigue en <c>numero + 1</c>, o sea si nadie tomó otro
    /// número después. Devuelve las filas afectadas.</summary>
    private async Task<int> DevolverNumeroAsync(int idPuntoVenta, short codigoAfip, long numero, CancellationToken ct)
    {
        await using var comando = await NuevoComandoAsync(
            "UPDATE numeraciones_fiscales SET proximo_numero = $3 " +
            "WHERE id_punto_venta = $1 AND codigo_afip = $2 AND proximo_numero = $4",
            ct, idPuntoVenta, codigoAfip, numero, numero + 1);
        return await comando.ExecuteNonQueryAsync(ct);
    }

    /// <summary>Borrado físico del espejo rechazado: ARCA no lo registró, así que no queda comprobante fiscal (regla 1).
    /// Solo borra los ítems y la fila de ESE espejo.</summary>
    private async Task BorrarEspejoAsync(int idEspejo, CancellationToken ct)
    {
        await using (var items = await NuevoComandoAsync(
            "DELETE FROM items_comprobante_venta WHERE id_comprobante_venta = $1", ct, idEspejo))
        {
            await items.ExecuteNonQueryAsync(ct);
        }

        await using var espejo = await NuevoComandoAsync(
            "DELETE FROM comprobantes_venta WHERE id_comprobante_venta = $1", ct, idEspejo);
        await espejo.ExecuteNonQueryAsync(ct);
    }

    // ---- Acumulado (SQL crudo: SaveChanges rechaza la tabla) ---------------------------------------

    /// <summary>Lo que cada medio cobró en la venta (<c>importe − vuelto</c>, solo montos positivos): la misma lectura con la
    /// que la decisión alimentó el acumulado.</summary>
    private async Task<Dictionary<int, decimal>> LeerVendidoPorMedioAsync(int idVenta, CancellationToken ct)
    {
        await using var comando = await NuevoComandoAsync(
            "SELECT p.id_medio_pago, p.importe - p.vuelto " +
            "FROM pagos_comprobante p " +
            "WHERE p.id_comprobante_venta = $1 AND p.deleted_at IS NULL",
            ct, idVenta);

        var vendido = new Dictionary<int, decimal>();
        await using var lector = await comando.ExecuteReaderAsync(ct);
        while (await lector.ReadAsync(ct))
        {
            var monto = lector.GetDecimal(1);
            if (monto > 0m)
            {
                vendido[lector.GetInt32(0)] = vendido.GetValueOrDefault(lector.GetInt32(0)) + monto;
            }
        }

        return vendido;
    }

    private async Task AplicarAlAcumuladoAsync(
        int idEmpresa, int idMedioPago, DateOnly periodo, decimal saldo, decimal vendido, decimal facturado,
        CancellationToken ct)
    {
        await using var comando = await NuevoComandoAsync(
            "UPDATE acumulados_de_facturacion SET saldo_a_facturar = saldo_a_facturar + $4, " +
            "total_vendido = total_vendido + $5, total_facturado = total_facturado + $6 " +
            "WHERE id_empresa = $1 AND id_medio_pago = $2 AND periodo = $3",
            ct, idEmpresa, idMedioPago, periodo, saldo, vendido, facturado);
        if (await comando.ExecuteNonQueryAsync(ct) != 1)
        {
            throw new InvalidOperationException(
                $"No existe el acumulado de facturación del medio {idMedioPago} para el período {periodo:yyyy-MM}.");
        }
    }

    // ---- Proyección ----------------------------------------------------------------------------------

    private async Task<EstadoFiscalDeVenta> ProyectarAsync(
        FacturacionPendiente decision, string? errorTransitorio, CancellationToken ct)
    {
        var estado = decision.Estado switch
        {
            Pendiente => EstadoFacturacionDeVenta.Pendiente,
            Emitida => EstadoFacturacionDeVenta.Emitida,
            Rechazada => EstadoFacturacionDeVenta.Rechazada,
            Cancelada => EstadoFacturacionDeVenta.Cancelada,
            var otro => throw new InvalidOperationException($"Estado de facturación desconocido: {otro}.")
        };

        var comprobante = estado == EstadoFacturacionDeVenta.Emitida
            ? await LeerFacturaAsync(decision.IdComprobanteFiscal!.Value, ct)
            : null;

        return new EstadoFiscalDeVenta(
            EstadosFiscalesDeVenta.De(estado), decision.Motivo, decision.Id, errorTransitorio ?? decision.UltimoError,
            comprobante);
    }

    private async Task<DatosDeFacturaFiscal> LeerFacturaAsync(int idEspejo, CancellationToken ct)
    {
        var espejo = await db.ComprobantesVenta.AsNoTracking().FirstAsync(c => c.Id == idEspejo, ct);
        var puntoVenta = await db.PuntosVenta.AsNoTracking().FirstAsync(p => p.Id == espejo.IdPuntoVenta, ct);
        var empresa = await db.Empresas.AsNoTracking().FirstAsync(e => e.Id == puntoVenta.IdEmpresa, ct);
        var cliente = await db.Clientes.AsNoTracking().FirstAsync(c => c.Id == espejo.IdCliente, ct);
        var tipo = await db.TiposComprobante.AsNoTracking().FirstAsync(t => t.Id == espejo.IdTipoComprobante, ct);

        var fechaFiscal = await fiscal.ResolverFechaFiscalAsync(empresa.Id, espejo.Fecha, ct);
        var (tipoDoc, nroDoc) = ServicioDeFacturacionFiscal.MapearDocumentoArca(cliente);
        var qr = ServicioDeFacturacionFiscal.ConstruirQrSiCorresponde(
            new RespuestaCae(espejo.ResultadoFiscal ?? ResultadoFiscal.Pendiente, espejo.Cae, espejo.CaeVencimiento, [], []),
            fechaFiscal, ServicioDeFacturacionFiscal.ExigirCuitNumerico(empresa), puntoVenta.NumeroFiscal!.Value,
            tipo.CodigoAfip!.Value, espejo.Numero, espejo.Total, tipoDoc, nroDoc);

        return new DatosDeFacturaFiscal(
            espejo.Id, tipo.Codigo, tipo.Letra!.Value, puntoVenta.NumeroFiscal.Value, espejo.Numero, fechaFiscal,
            espejo.Cae, espejo.CaeVencimiento, qr, LeerObservaciones(espejo.ObservacionesFiscales));
    }

    private static IReadOnlyList<ObservacionArca> LeerObservaciones(string? json)
    {
        if (string.IsNullOrWhiteSpace(json))
        {
            return [];
        }

        using var documento = JsonDocument.Parse(json);
        return
        [
            .. documento.RootElement.EnumerateArray().Select(o => new ObservacionArca(
                o.GetProperty("codigo").GetInt32(), o.GetProperty("mensaje").GetString() ?? string.Empty))
        ];
    }

    // ---- Comandos -------------------------------------------------------------------------------------

    /// <summary>Un comando sobre la conexión del contexto, dentro de la transacción actual si la hay. Los parámetros
    /// <c>null</c> viajan como <c>NULL</c>.</summary>
    private async Task<DbCommand> NuevoComandoAsync(string sql, CancellationToken ct, params object?[] parametros)
    {
        var conexion = db.Database.GetDbConnection();
        if (conexion.State != System.Data.ConnectionState.Open)
        {
            await db.Database.OpenConnectionAsync(ct);
        }

        var comando = conexion.CreateCommand();
        comando.Transaction = db.Database.CurrentTransaction?.GetDbTransaction();
        comando.CommandText = sql;
        foreach (var parametro in parametros)
        {
            ParametrosDeComando.AgregarNulo(comando, parametro);
        }

        return comando;
    }
}
