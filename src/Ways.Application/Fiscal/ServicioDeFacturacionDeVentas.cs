using System.Data.Common;
using System.Globalization;
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
/// ARCA puede autorizar un número aunque nunca recibamos la respuesta, así que el número tiene que sobrevivir a la falta de
/// respuesta (la emisión directa, <see cref="ServicioDeFacturacionFiscal"/>, lo deshace todo ante ese caso). Cada paso es su
/// propia transacción, sin reintento (un reintento tras un commit ambiguo quemaría otro número), y cada uno retoma el lock de
/// la serie:
///   A) crea el espejo: lock de la serie, verifica que no la detenga otro espejo pendiente, bloquea la venta y, bajo ese lock,
///      relee que la venta siga emitida, que no tenga ya un espejo y que su decisión siga pendiente; toma el número e inserta
///      el espejo en <c>pendiente</c>. Si algo de eso dejó de valer no inserta nada ni toma número.
///   C) consulta un espejo pendiente que ya existía (de una llamada anterior). Encontrado: lo adopta. No encontrado: guarda la
///      fecha de hoy en una transacción propia ya comiteada, ANTES de pedir. Cualquier otro error de la consulta no es definitivo.
///   S) pide el CAE con la fecha ya guardada y aplica el resultado. Solo corre si el espejo no cambió desde el paso anterior de
///      la misma llamada (la época, o <c>xmin</c>, de su fila): así ninguna otra llamada pudo consultar o pedir en el medio.
/// Un espejo pendiente SIEMPRE se resuelve, sea cual sea el estado de su decisión: si la decisión ya no está pendiente se
/// consulta y se adopta o se libera, nunca se pide un CAE nuevo.
///
/// Orden de locks: <c>numeraciones_fiscales</c> → <c>comprobantes_venta</c> (venta, luego espejo) → <c>facturaciones_de_venta</c>
/// → <c>acumulados_de_facturacion</c> (por medio de pago ascendente). Toda transición de una decisión se hace con su fila
/// bloqueada y su estado releído bajo ese lock; el registro de un fallo es la excepción: un único UPDATE guardado por
/// <c>estado = 'pendiente'</c>, sin lock previo. El acumulado solo se escribe con SQL crudo, igual que en
/// <see cref="ServicioDeDecisionDeFacturacion"/>, y cada reversión es la contraparte exacta de lo que aplicó esa decisión.
///
/// Un resultado es definitivo solo si está en la lista blanca: una <see cref="RespuestaCae"/> (aprobado, aprobado con
/// observaciones o rechazado), o el error <c>arca_rechazo</c> SI la consulta posterior confirma que ARCA no tiene el número.
/// Todo lo demás (transporte, falla SOAP, ticket de acceso, numeración desincronizada, un error de la consulta) deja el espejo
/// pendiente con su número.
///
/// La espera entre intentos (<c>proximo_intento_en</c>) se REGISTRA acá y la respeta el procesador por lote
/// (<see cref="ProcesadorDeFacturacionesPorLote"/>); este servicio procesa la decisión que se le pide aunque su espera no haya
/// vencido. Si el token que recibe se cancela (el presupuesto del lote), el corte no se anota como intento: todos los registros de
/// fallo exigen que el token siga vivo.
/// </summary>
public class ServicioDeFacturacionDeVentas(
    IWaysDbContext db,
    IRelojDelSistema reloj,
    IContextoDeUsuario contexto,
    ServicioDeFacturacionFiscal fiscal,
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

    /// <summary>Procesa la decisión: devuelve su estado resultante. Idempotente: una decisión que ya no está pendiente y no
    /// tiene un espejo pendiente devuelve su estado sin ninguna llamada a ARCA. Una decisión inexistente o de otro tenant es
    /// un 404.</summary>
    public async Task<EstadoFiscalDeVenta> ProcesarDecisionAsync(int idFacturacion, CancellationToken ct = default)
    {
        var idTenant = contexto.IdTenant
            ?? throw new InvalidOperationException(
                "ServicioDeFacturacionDeVentas requiere un actor de tenant; OperacionDePos no admite plataforma.");

        return await ProcesarAsync(idFacturacion, idTenant, ct);
    }

    /// <summary>Evalúa el estado actual y lo avanza. Se vuelve a llamar a sí mismo ("reevaluar") solo cuando otra llamada
    /// comiteó un cambio entre las lecturas sin lock y el lock; cada reevaluación presupone una transición nueva y comiteada
    /// de otra llamada, así que no hay un ciclo sin otro escritor.</summary>
    private async Task<EstadoFiscalDeVenta> ProcesarAsync(int idFacturacion, int idTenant, CancellationToken ct)
    {
        db.ChangeTracker.Clear();

        var decision = await LeerDecisionAsync(idFacturacion, ct)
            ?? throw ErrorDominio.NoEncontrado($"No existe la facturación {idFacturacion}.");
        var venta = await db.ComprobantesVenta.AsNoTracking()
            .FirstAsync(c => c.Id == decision.IdComprobanteVenta, ct);
        var espejo = await db.ComprobantesVenta.AsNoTracking()
            .FirstOrDefaultAsync(c => c.IdVentaOrigen == decision.IdComprobanteVenta, ct);

        // Un espejo pendiente se resuelve aunque su decisión ya no lo esté.
        if (espejo is { ResultadoFiscal: ResultadoFiscal.Pendiente })
        {
            return await ProcesarEspejoPendienteAsync(decision, espejo, idTenant, ct);
        }

        if (decision.Estado != Pendiente)
        {
            return await ProyectarAsync(decision, null, ct);
        }

        if (espejo is not null)
        {
            // Otra llamada pudo comitear toda la resolución entre la lectura de la decisión y la del espejo: se relee una vez.
            var actual = await LeerDecisionAsync(idFacturacion, ct)
                ?? throw new InvalidOperationException($"La decisión {idFacturacion} dejó de existir.");
            if (actual.Estado != Pendiente)
            {
                return await ProyectarAsync(actual, null, ct);
            }

            throw new InvalidOperationException(
                $"El espejo {espejo.Id} está '{espejo.ResultadoFiscal}' pero su decisión {decision.Id} sigue pendiente.");
        }

        // Una venta anulada es terminal (emitido → anulado) y un espejo no se crea sobre ella, así que esta lectura sin lock
        // no puede quedar vieja: lo que decide la anulación contra el espejo se decide bajo el lock de la venta.
        if (venta.Estado != EstadoComprobante.Emitido)
        {
            return await CancelarAsync(decision, ct);
        }

        var evaluacion = await EvaluarCompuertasAsync(venta, false, ct);
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

        var creacion = await CrearEspejoAsync(decision, venta, plan, idTenant, ct);
        switch (creacion.Resultado)
        {
            case ResultadoDeCreacion.VentaNoEmitida:
                return await CancelarAsync(decision, ct);
            case ResultadoDeCreacion.Obsoleta:
                return await ProcesarAsync(idFacturacion, idTenant, ct);
            case ResultadoDeCreacion.SerieOcupada:
                return await ProyectarAsync(decision, $"{SerieOcupada}: otra llamada está usando la serie.", ct);
            case ResultadoDeCreacion.SerieBloqueada:
                return await ProyectarAsync(
                    decision, $"{SerieBloqueada}: la serie espera que otro comprobante pendiente se resuelva.", ct);
        }

        return await SolicitarAsync(decision, plan, creacion.Espejo!.Id, creacion.Espejo.Epoca, ticket, idTenant, ct);
    }

    /// <summary>Un espejo pendiente de una llamada anterior. Para consultarlo solo hace falta lo que la consulta usa (certificado,
    /// CUIT del emisor, punto de venta fiscal, tipo y número), no las compuertas de la venta: si ARCA ya tiene el número se
    /// adopta aunque la configuración haya cambiado. Las compuertas completas se aplican solo si ARCA no lo tiene y la decisión
    /// sigue pendiente, antes de volver a pedir.</summary>
    private async Task<EstadoFiscalDeVenta> ProcesarEspejoPendienteAsync(
        FacturacionPendiente decision, ComprobanteVenta espejo, int idTenant, CancellationToken ct)
    {
        var prerrequisitos = await EvaluarPrerrequisitosDeConsultaAsync(espejo, ct);

        if (prerrequisitos.Fallo is { } sinPrerrequisitos)
        {
            var compuertas = decision.Estado == Pendiente ? await EvaluarCompuertasAsync(espejo, true, ct) : null;
            var fallo = compuertas?.Fallo ?? sinPrerrequisitos;
            return await RegistrarFalloDeEspejoAsync(
                decision, espejo.Id, $"{fallo.Codigo}: {fallo.Mensaje}", EsperaInicialDeCompuerta, EsperaMaximaDeCompuerta, ct);
        }

        var consulta = prerrequisitos.Contexto!;
        TicketDeAcceso ticket;
        try
        {
            ticket = await fiscal.ObtenerTicketAsync(consulta.Empresa, consulta.Ambiente, ct);
        }
        catch (Exception ex) when (!ct.IsCancellationRequested)
        {
            return await RegistrarFalloDeEspejoAsync(
                decision, espejo.Id, DescribirFalla(ex), EsperaInicialNoDefinitiva, EsperaMaximaNoDefinitiva, ct);
        }

        return await ConsultarEspejoPendienteAsync(decision, consulta, espejo, ticket, idTenant, ct);
    }

    // ---- Compuertas (sin red) --------------------------------------------------------------------

    private sealed record FalloDeCompuerta(string Codigo, string Mensaje);

    /// <summary>Lo que hace falta para identificar el comprobante ante ARCA y autenticarse: se arma sin las compuertas de la venta.</summary>
    private sealed record ContextoDeConsulta(
        PuntoVenta PuntoVenta, Empresa Empresa, AmbienteFiscal Ambiente, string CuitEmisor, short CodigoAfip)
    {
        public ClaveDeSerie Serie => new(PuntoVenta.NumeroFiscal!.Value, CodigoAfip);
    }

    private sealed record Prerrequisitos(ContextoDeConsulta? Contexto, FalloDeCompuerta? Fallo);

    private sealed record Plan(
        PuntoVenta PuntoVenta, Empresa Empresa, TipoComprobante Tipo, char Letra, string CuitEmisor,
        short CondicionIvaReceptor, short TipoDocReceptor, long NroDocReceptor, TotalesFiscales Totales,
        AmbienteFiscal Ambiente)
    {
        public ClaveDeSerie Serie => new(PuntoVenta.NumeroFiscal!.Value, Tipo.CodigoAfip!.Value);

        public ContextoDeConsulta Consulta => new(PuntoVenta, Empresa, Ambiente, CuitEmisor, Tipo.CodigoAfip!.Value);
    }

    private sealed record Evaluacion(Plan? Plan, FalloDeCompuerta? Fallo);

    private static FalloDeCompuerta SinNumeroFiscal() =>
        new("punto_venta_sin_numero_fiscal", "El punto de venta no tiene cargado su número fiscal ARCA.");

    private static FalloDeCompuerta SinCertificado() =>
        new("certificado_fiscal_ausente", "No hay un certificado fiscal activo para esta empresa y ambiente.");

    /// <summary>Lo mínimo para consultar un espejo ya creado: punto de venta fiscal, certificado activo, CUIT del emisor y el
    /// tipo del propio espejo.</summary>
    private async Task<Prerrequisitos> EvaluarPrerrequisitosDeConsultaAsync(ComprobanteVenta espejo, CancellationToken ct)
    {
        static Prerrequisitos Falla(FalloDeCompuerta fallo) => new(null, fallo);

        var ambiente = fiscal.ResolverAmbiente();
        var puntoVenta = await db.PuntosVenta.AsNoTracking().FirstAsync(p => p.Id == espejo.IdPuntoVenta, ct);
        var empresa = await db.Empresas.AsNoTracking().FirstAsync(e => e.Id == puntoVenta.IdEmpresa, ct);

        if (puntoVenta.NumeroFiscal is null)
        {
            return Falla(SinNumeroFiscal());
        }

        var hayCertificadoActivo = await db.CertificadosFiscales.AsNoTracking()
            .AnyAsync(c => c.IdEmpresa == empresa.Id && c.Ambiente == ambiente && c.Activo, ct);
        if (!hayCertificadoActivo)
        {
            return Falla(SinCertificado());
        }

        string cuitEmisor;
        try
        {
            cuitEmisor = ServicioDeFacturacionFiscal.ExigirCuitNumerico(empresa);
        }
        catch (ErrorDominio ex) when (ex.Codigo == "empresa_sin_cuit_valido")
        {
            return Falla(new FalloDeCompuerta(ex.Codigo, ex.Message));
        }

        var tipo = await db.TiposComprobante.AsNoTracking().FirstAsync(t => t.Id == espejo.IdTipoComprobante, ct);
        if (tipo.CodigoAfip is not { } codigoAfip)
        {
            return Falla(new FalloDeCompuerta(
                "tipo_fiscal_invalido", $"El tipo del espejo ('{tipo.Codigo}') no tiene código ARCA."));
        }

        return new Prerrequisitos(new ContextoDeConsulta(puntoVenta, empresa, ambiente, cuitEmisor, codigoAfip), null);
    }

    /// <summary>Las compuertas se evalúan sobre el ticket (si todavía no hay espejo) o sobre el espejo existente, cuyo tipo,
    /// cliente e ítems ya quedaron fijados por la llamada que lo creó.</summary>
    private async Task<Evaluacion> EvaluarCompuertasAsync(ComprobanteVenta origen, bool origenEsEspejo, CancellationToken ct)
    {
        static Evaluacion Falla(FalloDeCompuerta fallo) => new(null, fallo);
        static Evaluacion FallaCon(string codigo, string mensaje) => Falla(new FalloDeCompuerta(codigo, mensaje));

        var ambiente = fiscal.ResolverAmbiente();
        var puntoVenta = await db.PuntosVenta.AsNoTracking().FirstAsync(p => p.Id == origen.IdPuntoVenta, ct);
        var empresa = await db.Empresas.AsNoTracking().FirstAsync(e => e.Id == puntoVenta.IdEmpresa, ct);

        if (empresa.IdCondicionFiscal is not { } idCondicionEmisor)
        {
            return FallaCon("empresa_sin_condicion_fiscal", "La empresa no tiene cargada su condición fiscal ARCA.");
        }

        if (puntoVenta.NumeroFiscal is null)
        {
            return Falla(SinNumeroFiscal());
        }

        var cliente = await db.Clientes.AsNoTracking().FirstAsync(c => c.Id == origen.IdCliente, ct);
        var condicionEmisor = await db.CondicionesFiscales.AsNoTracking().FirstAsync(c => c.Id == idCondicionEmisor, ct);
        var condicionReceptor = await db.CondicionesFiscales.AsNoTracking().FirstAsync(c => c.Id == cliente.IdCondicionFiscal, ct);

        if (condicionReceptor.Codigo == "NO_RESP" || condicionReceptor.CodigoAfip is null)
        {
            return FallaCon(
                "condicion_fiscal_receptor_no_mapeada",
                $"La condición fiscal del receptor ('{condicionReceptor.Codigo}') no tiene mapeo ARCA confirmado.");
        }

        var hayCertificadoActivo = await db.CertificadosFiscales.AsNoTracking()
            .AnyAsync(c => c.IdEmpresa == empresa.Id && c.Ambiente == ambiente && c.Activo, ct);
        if (!hayCertificadoActivo)
        {
            return Falla(SinCertificado());
        }

        string cuitEmisor;
        try
        {
            cuitEmisor = ServicioDeFacturacionFiscal.ExigirCuitNumerico(empresa);
        }
        catch (ErrorDominio ex) when (ex.Codigo == "empresa_sin_cuit_valido")
        {
            return FallaCon(ex.Codigo, ex.Message);
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
            return FallaCon("tipo_fiscal_invalido", $"No hay un tipo de factura fiscal activo para la letra '{letra}'.");
        }

        var (tipoDoc, nroDoc) = ServicioDeFacturacionFiscal.MapearDocumentoArca(cliente);
        const short DocumentoCuit = 80;
        if (letra == 'A' && tipoDoc != DocumentoCuit)
        {
            return FallaCon("receptor_sin_cuit", "Una factura A exige que el receptor esté identificado con su CUIT.");
        }

        var lineas = await fiscal.ComponerLineasFiscalesDesdeItemsAsync(origen.Id, ct);
        TotalesFiscales totales;
        try
        {
            totales = ComposicionDeTotalesFiscales.Componer(lineas);
        }
        catch (ErrorDominio ex) when (ex.Codigo == "alicuota_sin_mapeo_afip")
        {
            return FallaCon(ex.Codigo, ex.Message);
        }

        if (totales.ImpTotal != origen.Total)
        {
            return FallaCon(
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
        VentaNoEmitida,
        Obsoleta
    }

    /// <summary><paramref name="Epoca"/> es el <c>xmin</c> de la fila del espejo cuando el paso la dejó: el paso siguiente de la
    /// misma llamada solo sigue si la fila no cambió.</summary>
    private sealed record EspejoEnTramite(int Id, long Epoca);

    private sealed record Creacion(ResultadoDeCreacion Resultado, EspejoEnTramite? Espejo);

    private async Task<Creacion> CrearEspejoAsync(
        FacturacionPendiente decision, ComprobanteVenta venta, Plan plan, int idTenant, CancellationToken ct)
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

            // El lock de la venta serializa contra la anulación: o la anulación ya comiteó y la venta deja de estar
            // emitida, o este espejo comitea primero y la anulación lo encuentra.
            if (await BloquearVentaYLeerEstadoAsync(venta.Id, ct) != "emitido")
            {
                return new Creacion(ResultadoDeCreacion.VentaNoEmitida, null);
            }

            // Lo que se leyó sin lock al empezar pudo quedar viejo: otra llamada pudo crear y resolver un espejo, o resolver
            // la decisión, antes de que esta tomara los locks. Se relee bajo ellos, antes de tomar el número y antes de
            // preguntar por la serie: un espejo propio de esta venta no es "serie bloqueada" sino un estado que se reevalúa.
            if (await ExisteEspejoDeLaVentaAsync(venta.Id, ct)
                || await BloquearDecisionYLeerEstadoAsync(decision.Id, ct) != Pendiente)
            {
                return new Creacion(ResultadoDeCreacion.Obsoleta, null);
            }

            if (await HayEspejoPendienteEnLaSerieAsync(plan.PuntoVenta.Id, codigoAfip, ct))
            {
                return new Creacion(ResultadoDeCreacion.SerieBloqueada, null);
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
            var epoca = await LeerEpocaDelEspejoAsync(espejo.Id, ct);
            await transaccion.CommitAsync(ct);
            db.ChangeTracker.Clear();

            return new Creacion(ResultadoDeCreacion.Creado, new EspejoEnTramite(espejo.Id, epoca));
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

    /// <summary>¿Tiene la venta un espejo, en cualquier estado? El índice único <c>ux_comprobantes_venta_venta_origen</c> admite
    /// uno solo: esta pregunta, hecha bajo el lock de la venta, evita llegar a él.</summary>
    private async Task<bool> ExisteEspejoDeLaVentaAsync(int idVenta, CancellationToken ct)
    {
        await using var comando = await NuevoComandoAsync(
            "SELECT EXISTS (SELECT 1 FROM comprobantes_venta WHERE id_venta_origen = $1)", ct, idVenta);
        return (bool)(await comando.ExecuteScalarAsync(ct))!;
    }

    private async Task<long> LeerEpocaDelEspejoAsync(int idEspejo, CancellationToken ct)
    {
        await using var comando = await NuevoComandoAsync(
            "SELECT xmin::text FROM comprobantes_venta WHERE id_comprobante_venta = $1", ct, idEspejo);
        return long.Parse((string)(await comando.ExecuteScalarAsync(ct))!, CultureInfo.InvariantCulture);
    }

    // ---- Transacciones C y S: consulta, fecha, CAE y resultado ---------------------------------------

    private abstract record Desenlace;

    private sealed record Definitivo(RespuestaCae Respuesta, DateOnly? FechaDeArca = null) : Desenlace;

    private sealed record NoDefinitivo(string Descripcion) : Desenlace;

    private sealed record NoEncontrado : Desenlace;

    /// <summary>ARCA no tiene el número y la decisión ya no está pendiente: el espejo se libera sin pedir nada.</summary>
    private sealed record Liberar : Desenlace;

    /// <summary>Lo que una transacción deja para después del commit (logs, reevaluación, época del paso siguiente): nada se
    /// loguea desde dentro de una transacción que todavía puede deshacerse.</summary>
    private sealed class Salida
    {
        public string? ErrorSinPenalidad { get; set; }

        public bool Reevaluar { get; set; }

        public long? EpocaParaSolicitar { get; set; }

        public Plan? PlanParaSolicitar { get; set; }

        public (long Numero, string Detalle)? GuardaDelNumero { get; set; }

        public (string EstadoDeLaDecision, string Detalle)? Inconsistencia { get; set; }

        public List<(int IdMedioPago, DateOnly Periodo)> AcumuladosFaltantes { get; } = [];
    }

    /// <summary>Paso C: el espejo pendiente existía de antes. Se consulta a ARCA antes que nada (I2).</summary>
    private async Task<EstadoFiscalDeVenta> ConsultarEspejoPendienteAsync(
        FacturacionPendiente decision, ContextoDeConsulta consulta, ComprobanteVenta espejoLeido,
        TicketDeAcceso ticket, int idTenant, CancellationToken ct)
    {
        var idEspejo = espejoLeido.Id;
        var salida = new Salida();
        var estrategia = FabricaDeEstrategiaSinReintento.CrearEstrategiaSinReintento(db);

        await estrategia.ExecuteAsync(async () =>
        {
            await using var transaccion = await db.Database.BeginTransactionAsync(ct);

            if (!await TomarLockDeLaSerieAsync(consulta.PuntoVenta.Id, consulta.CodigoAfip, ct))
            {
                salida.ErrorSinPenalidad = $"{SerieOcupada}: otra llamada está usando la serie.";
                return;
            }

            var espejo = await BloquearYLeerEspejoAsync(idEspejo, ct);
            if (espejo is not { Estado: Pendiente })
            {
                salida.Reevaluar = true;
                return;
            }

            Desenlace desenlace;
            (desenlace, ticket) = await ConsultarEnArcaAsync(consulta, ticket, espejo.Numero, ct);
            if (desenlace is NoEncontrado)
            {
                var estadoDeLaDecision = await BloquearDecisionYLeerEstadoAsync(decision.Id, ct);
                if (estadoDeLaDecision != Pendiente)
                {
                    await AplicarAsync(
                        decision, estadoDeLaDecision, consulta, idEspejo, espejo, new Liberar(), idTenant, salida, ct);
                }
                else if (decision.Estado != Pendiente)
                {
                    salida.Reevaluar = true;
                    return;
                }
                else
                {
                    // Las compuertas de la venta se evalúan solo ahora que ARCA no tiene el número y hay que volver a pedir.
                    var compuertas = await EvaluarCompuertasAsync(espejoLeido, true, ct);
                    if (compuertas.Fallo is { } fallo)
                    {
                        _ = await EscribirFalloAsync(
                            decision.Id, $"{fallo.Codigo}: {fallo.Mensaje}", EsperaInicialDeCompuerta, EsperaMaximaDeCompuerta, ct);
                    }
                    else
                    {
                        // Se va a pedir con la fecha de hoy: se guarda ahora, en una transacción que comitea antes de la
                        // llamada, para que el espejo nunca quede con una fecha distinta de la que viajó.
                        salida.EpocaParaSolicitar = await RefrescarFechaDelEspejoAsync(idEspejo, reloj.Ahora, ct);
                        salida.PlanParaSolicitar = compuertas.Plan;
                    }
                }
            }
            else
            {
                await BloquearYAplicarAsync(decision, consulta, idEspejo, espejo, desenlace, idTenant, salida, ct);
            }

            await transaccion.CommitAsync(ct);
        });

        if (salida.EpocaParaSolicitar is { } epoca)
        {
            return await SolicitarAsync(decision, salida.PlanParaSolicitar!, idEspejo, epoca, ticket, idTenant, ct);
        }

        return await CerrarAsync(decision, consulta, idEspejo, salida, idTenant, ct);
    }

    /// <summary>Paso S: pide el CAE con la fecha que el espejo ya tiene guardada, solo si el espejo no cambió desde el paso
    /// anterior de esta llamada.</summary>
    private async Task<EstadoFiscalDeVenta> SolicitarAsync(
        FacturacionPendiente decision, Plan plan, int idEspejo, long epocaEsperada, TicketDeAcceso ticket, int idTenant,
        CancellationToken ct)
    {
        var consulta = plan.Consulta;
        var salida = new Salida();
        var estrategia = FabricaDeEstrategiaSinReintento.CrearEstrategiaSinReintento(db);

        await estrategia.ExecuteAsync(async () =>
        {
            await using var transaccion = await db.Database.BeginTransactionAsync(ct);

            if (!await TomarLockDeLaSerieAsync(consulta.PuntoVenta.Id, consulta.CodigoAfip, ct))
            {
                salida.ErrorSinPenalidad = $"{SerieOcupada}: otra llamada está usando la serie.";
                return;
            }

            // Bajo el lock de la serie: otra llamada pudo resolver o liberar el espejo, o tocarlo (y entonces consultó o pidió por
            // su cuenta: cualquier cambio de la fila cambia su época), o la decisión pudo dejar de estar pendiente. En ninguno de
            // los casos se pide: se evalúa de nuevo.
            var espejo = await BloquearYLeerEspejoAsync(idEspejo, ct);
            if (espejo?.Epoca != epocaEsperada || await LeerEstadoDeLaDecisionAsync(decision.Id, ct) != Pendiente)
            {
                salida.Reevaluar = true;
                return;
            }

            var desenlace = await PedirCaeAsync(plan, idEspejo, espejo, ticket, ct);
            await BloquearYAplicarAsync(decision, consulta, idEspejo, espejo, desenlace, idTenant, salida, ct);
            await transaccion.CommitAsync(ct);
        });

        return await CerrarAsync(decision, consulta, idEspejo, salida, idTenant, ct);
    }

    private async Task<EstadoFiscalDeVenta> CerrarAsync(
        FacturacionPendiente decision, ContextoDeConsulta consulta, int idEspejo, Salida salida, int idTenant,
        CancellationToken ct)
    {
        if (salida.GuardaDelNumero is { } guarda)
        {
            log.LogError(
                "No se pudo devolver el número {Numero} del espejo {IdEspejo} a la serie (punto de venta {IdPuntoVenta}, " +
                "tipo {CodigoAfip}): {Detalle} El espejo queda rechazado con su número y la serie queda desincronizada con ARCA.",
                guarda.Numero, idEspejo, consulta.PuntoVenta.Id, consulta.CodigoAfip, guarda.Detalle);
        }

        if (salida.Inconsistencia is { } inconsistencia)
        {
            log.LogError(
                "La decisión {IdFacturacion} está '{Estado}' pero el espejo {IdEspejo} de su venta seguía pendiente. " +
                "Se resolvió el espejo ({Detalle}) y no se tocó el estado de la decisión.",
                decision.Id, inconsistencia.EstadoDeLaDecision, idEspejo, inconsistencia.Detalle);
        }

        LoguearAcumuladosFaltantes(decision.Id, salida);

        if (salida.Reevaluar)
        {
            return await ProcesarAsync(decision.Id, idTenant, ct);
        }

        db.ChangeTracker.Clear();
        var final = await LeerDecisionAsync(decision.Id, ct)
            ?? throw new InvalidOperationException($"La decisión {decision.Id} dejó de existir.");
        return await ProyectarAsync(final, salida.ErrorSinPenalidad, ct);
    }

    private void LoguearAcumuladosFaltantes(int idFacturacion, Salida salida)
    {
        foreach (var (idMedioPago, periodo) in salida.AcumuladosFaltantes)
        {
            log.LogError(
                "No existe el acumulado de facturación del medio {IdMedioPago} para el período {Periodo:yyyy-MM}: la decisión " +
                "{IdFacturacion} no pudo ajustarlo y el resto de la resolución siguió adelante.",
                idMedioPago, periodo, idFacturacion);
        }
    }

    /// <summary>Consulta a ARCA si ya tiene el número. "No encontrado" es solo la respuesta explícita de comprobante
    /// inexistente; cualquier otro error de la consulta es no definitivo.</summary>
    private async Task<(Desenlace Desenlace, TicketDeAcceso Ticket)> ConsultarEnArcaAsync(
        ContextoDeConsulta consulta, TicketDeAcceso ticket, long numero, CancellationToken ct)
    {
        ConsultaDeComprobante respuesta;
        try
        {
            (respuesta, ticket) = await fiscal.ConsultarConReintentoDeTicketAsync(
                consulta.Empresa, consulta.Ambiente, consulta.CuitEmisor, consulta.Serie, numero, ticket, ct);
        }
        catch (Exception ex) when (!ct.IsCancellationRequested)
        {
            return (new NoDefinitivo(DescribirFalla(ex)), ticket);
        }

        Desenlace desenlace = respuesta.Encontrado
            ? new Definitivo(
                new RespuestaCae(respuesta.Resultado!.Value, respuesta.Cae, respuesta.CaeVencimiento, [], []),
                respuesta.Fecha)
            : new NoEncontrado();
        return (desenlace, ticket);
    }

    /// <summary>Pide el CAE del número del espejo con la fecha que el espejo ya tiene. Devuelve un <see cref="Definitivo"/> solo
    /// para la lista blanca de resultados definitivos: una respuesta de ARCA, o el error de nivel de llamada <c>arca_rechazo</c>
    /// si la consulta del mismo número confirma que ARCA no lo tiene; cualquier otra falla es <see cref="NoDefinitivo"/>.</summary>
    private async Task<Desenlace> PedirCaeAsync(
        Plan plan, int idEspejo, EspejoBloqueado espejo, TicketDeAcceso ticket, CancellationToken ct)
    {
        var numero = espejo.Numero;
        var fechaFiscal = await fiscal.ResolverFechaFiscalAsync(plan.Empresa.Id, espejo.Fecha, ct);
        var totales = plan.Totales;
        var solicitud = new SolicitudDeCae(
            plan.Serie, numero, numero, 1, plan.TipoDocReceptor, plan.NroDocReceptor, fechaFiscal, totales.ImpTotal,
            totales.ImpTotConc, totales.ImpNeto, totales.ImpOpEx, totales.ImpTrib, totales.ImpIVA,
            plan.CondicionIvaReceptor, totales.Iva);

        try
        {
            return new Definitivo(await fiscal.SolicitarCaeConReintentoDeTicketAsync(
                plan.Empresa, plan.Ambiente, plan.CuitEmisor, idEspejo, numero, solicitud, ct));
        }
        catch (ErrorDominio ex) when (ex.Codigo == "arca_rechazo")
        {
            // Un error de nivel de llamada no prueba que ARCA no tenga el número (puede ser una falla interna posterior a
            // registrarlo): antes de liberarlo se le pregunta.
            var (previo, _) = await ConsultarEnArcaAsync(plan.Consulta, ticket, numero, ct);
            return previo switch
            {
                NoEncontrado => new Definitivo(new RespuestaCae(
                    ResultadoFiscal.Rechazado, null, null, [], [new ObservacionArca(0, ex.Message)])),
                var otro => otro
            };
        }
        catch (Exception ex) when (!ct.IsCancellationRequested)
        {
            return new NoDefinitivo(DescribirFalla(ex));
        }
    }

    private async Task BloquearYAplicarAsync(
        FacturacionPendiente decision, ContextoDeConsulta consulta, int idEspejo, EspejoBloqueado espejo, Desenlace desenlace,
        int idTenant, Salida salida, CancellationToken ct)
    {
        var estadoDeLaDecision = await BloquearDecisionYLeerEstadoAsync(decision.Id, ct);
        await AplicarAsync(decision, estadoDeLaDecision, consulta, idEspejo, espejo, desenlace, idTenant, salida, ct);
    }

    /// <summary>Aplica el desenlace con la decisión ya bloqueada y su estado releído bajo ese lock (<paramref name="estadoDeLaDecision"/>).
    /// El espejo se resuelve siempre; la decisión solo se mueve si está pendiente. Si no lo está, el espejo igual se resuelve, la
    /// inconsistencia se loguea y la decisión conserva su estado (con el único agregado de enlazar el comprobante adoptado).</summary>
    private async Task AplicarAsync(
        FacturacionPendiente decision, string? estadoDeLaDecision, ContextoDeConsulta consulta, int idEspejo,
        EspejoBloqueado espejo, Desenlace desenlace, int idTenant, Salida salida, CancellationToken ct)
    {
        var pendiente = estadoDeLaDecision == Pendiente;

        switch (desenlace)
        {
            case Definitivo { Respuesta.Resultado: ResultadoFiscal.Rechazado } rechazo:
                await LiberarEspejoAsync(consulta, idEspejo, espejo.Numero, rechazo.Respuesta, idTenant, salida, ct);
                if (pendiente)
                {
                    await ActualizarDecisionAsync(
                        decision.Id, Rechazada, null, ConstruirTextoDeErrores(rechazo.Respuesta), ct);
                    var vendido = await LeerVendidoPorMedioAsync(decision.IdComprobanteVenta, ct);
                    foreach (var (idMedio, monto) in vendido.OrderBy(v => v.Key))
                    {
                        await AplicarAlAcumuladoAsync(
                            salida, decision.IdEmpresa, idMedio, decision.Periodo, monto, 0m, -monto, ct);
                    }
                }
                else
                {
                    salida.Inconsistencia = (
                        estadoDeLaDecision!, "ARCA rechazó el comprobante y el espejo se liberó");
                }

                break;

            case Definitivo aprobado:
                await AjustarFechaALaDeArcaAsync(consulta, idEspejo, espejo.Fecha, aprobado.FechaDeArca, ct);
                await fiscal.AplicarResultadoGuardadoAsync(idTenant, idEspejo, aprobado.Respuesta, ct);
                await AsignadorDeNumeroFiscal.ReconciliarAsync(
                    db, consulta.PuntoVenta.Id, consulta.CodigoAfip, espejo.Numero, reloj, ct);
                if (pendiente)
                {
                    await ActualizarDecisionAsync(decision.Id, Emitida, idEspejo, null, ct);
                }
                else
                {
                    var enlazada = await EnlazarComprobanteAsync(decision.Id, idEspejo, ct);
                    salida.Inconsistencia = (
                        estadoDeLaDecision!,
                        enlazada
                            ? "ARCA ya tenía el comprobante: se adoptó su CAE y se enlazó a la decisión"
                            : "ARCA ya tenía el comprobante: se adoptó su CAE y la decisión ya estaba enlazada a otro comprobante");
                }

                break;

            case Liberar:
                await LiberarEspejoAsync(
                    consulta, idEspejo, espejo.Numero, RespuestaDeLiberacion, idTenant, salida, ct);
                salida.Inconsistencia = (
                    estadoDeLaDecision!, "ARCA no tenía el número: el espejo se liberó sin pedir un CAE nuevo");
                break;

            case NoDefinitivo noDefinitivo:
                if (pendiente)
                {
                    _ = await EscribirFalloAsync(
                        decision.Id, noDefinitivo.Descripcion, EsperaInicialNoDefinitiva, EsperaMaximaNoDefinitiva, ct);
                }
                else
                {
                    salida.Inconsistencia = (
                        estadoDeLaDecision!, $"no hubo respuesta definitiva de ARCA y el espejo sigue pendiente: {noDefinitivo.Descripcion}");
                }

                break;
        }
    }

    private static readonly RespuestaCae RespuestaDeLiberacion = new(
        ResultadoFiscal.Rechazado, null, null, [],
        [new ObservacionArca(0, "El espejo se liberó sin respuesta de ARCA: su decisión ya no estaba pendiente y ARCA no tiene el número.")]);

    /// <summary>Libera el espejo: se borran sus ítems y su fila y el número vuelve a la serie (regla 1). Si el contador ya
    /// avanzó, el número no se puede devolver y el espejo queda rechazado con su número.</summary>
    private async Task LiberarEspejoAsync(
        ContextoDeConsulta consulta, int idEspejo, long numero, RespuestaCae respuesta, int idTenant, Salida salida,
        CancellationToken ct)
    {
        if (await DevolverNumeroAsync(consulta.PuntoVenta.Id, consulta.CodigoAfip, numero, ct) == 1)
        {
            await BorrarEspejoAsync(idEspejo, ct);
            return;
        }

        salida.GuardaDelNumero = (numero, $"El contador no estaba en {numero + 1}.");
        await fiscal.AplicarResultadoGuardadoAsync(idTenant, idEspejo, respuesta, ct);
    }

    /// <summary>Si ARCA registró el comprobante con una fecha distinta de la guardada, el espejo toma la de ARCA. Se compara el
    /// día fiscal de la empresa; si coincide no se toca nada. El instante nuevo es el mediodía de ese día en la zona
    /// fiscal.</summary>
    private async Task AjustarFechaALaDeArcaAsync(
        ContextoDeConsulta consulta, int idEspejo, DateTimeOffset fechaGuardada, DateOnly? fechaDeArca, CancellationToken ct)
    {
        if (fechaDeArca is not { } diaDeArca)
        {
            return;
        }

        var zona = await fiscal.ResolverZonaFiscalAsync(consulta.Empresa.Id, ct);
        if (FechaFiscal.DeInstante(fechaGuardada, zona) == diaDeArca)
        {
            return;
        }

        var mediodia = diaDeArca.ToDateTime(new TimeOnly(12, 0));
        var nueva = new DateTimeOffset(mediodia, zona.GetUtcOffset(mediodia));
        await using var comando = await NuevoComandoAsync(
            "UPDATE comprobantes_venta SET fecha = $2, updated_at = $3 WHERE id_comprobante_venta = $1",
            ct, idEspejo, nueva, reloj.Ahora);
        await comando.ExecuteNonQueryAsync(ct);
    }

    // ---- Cancelación -----------------------------------------------------------------------------

    private async Task<EstadoFiscalDeVenta> CancelarAsync(FacturacionPendiente decision, CancellationToken ct)
    {
        var salida = new Salida();
        var estrategia = FabricaDeEstrategiaSinReintento.CrearEstrategiaSinReintento(db);

        await estrategia.ExecuteAsync(async () =>
        {
            await using var transaccion = await db.Database.BeginTransactionAsync(ct);

            // Mismos locks y mismo orden que la creación del espejo: la venta primero, que la serializa contra ella, y
            // después, bajo ese lock, se relee que siga sin espejo y que la decisión siga pendiente.
            _ = await BloquearVentaYLeerEstadoAsync(decision.IdComprobanteVenta, ct);
            if (await ExisteEspejoDeLaVentaAsync(decision.IdComprobanteVenta, ct)
                || await BloquearDecisionYLeerEstadoAsync(decision.Id, ct) != Pendiente)
            {
                return;
            }

            await ActualizarDecisionAsync(decision.Id, Cancelada, null, null, ct);

            var aportes = JsonSerializer.Deserialize<List<AporteDeSaldo>>(decision.Aportes, OpcionesDeAportes)!;
            var vendido = await LeerVendidoPorMedioAsync(decision.IdComprobanteVenta, ct);
            foreach (var inverso in PoliticaDeFacturacionPorMedio.Revertir(aportes).OrderBy(a => a.IdMedioPago))
            {
                var monto = vendido.GetValueOrDefault(inverso.IdMedioPago);
                await AplicarAlAcumuladoAsync(
                    salida, decision.IdEmpresa, inverso.IdMedioPago, decision.Periodo, inverso.Aporte, -monto, -monto, ct);
            }

            await transaccion.CommitAsync(ct);
        });

        LoguearAcumuladosFaltantes(decision.Id, salida);
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

    /// <summary>Un espejo pendiente que no se puede consultar: si la decisión está pendiente se le anota el fallo; si no, no hay a
    /// quién anotárselo y queda el error en el log.</summary>
    private async Task<EstadoFiscalDeVenta> RegistrarFalloDeEspejoAsync(
        FacturacionPendiente decision, int idEspejo, string descripcion, TimeSpan espera, TimeSpan tope, CancellationToken ct)
    {
        if (decision.Estado == Pendiente)
        {
            return await RegistrarFalloAsync(decision, descripcion, espera, tope, ct);
        }

        log.LogError(
            "El espejo {IdEspejo} sigue pendiente y su decisión {IdFacturacion} está '{Estado}': no se pudo consultar a ARCA: {Detalle}",
            idEspejo, decision.Id, decision.Estado, descripcion);
        return await ProyectarAsync(decision, null, ct);
    }

    /// <summary>Suma un intento fallido a la decisión pendiente y registra cuándo volver a intentar:
    /// <c>espera × 2^intentos</c> con tope, contada desde el reloj del servicio. La lee el procesador por lote, no este servicio.
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

    /// <summary>Espera con la que el procesador por lote posterga una decisión cuyo procesamiento cortó su presupuesto.</summary>
    public static readonly TimeSpan EsperaPorCorteDeLote = TimeSpan.FromMinutes(1);

    /// <summary>Posterga una decisión cortada por el presupuesto del lote: solo fija <c>proximo_intento_en</c> a
    /// <see cref="EsperaPorCorteDeLote"/> desde ahora, sin tocar <c>intentos</c> ni <c>ultimo_error</c> (el corte no es un fallo).
    /// Un único UPDATE guardado por <c>estado = 'pendiente'</c>: una decisión que ya no está pendiente (por ejemplo, la que
    /// solo conserva un espejo pendiente) no se puede postergar. Nunca lanza; <paramref name="ct"/> tiene que ser un token
    /// propio y corto, porque el del lote ya está cancelado.</summary>
    public async Task PostergarPorCorteDeLoteAsync(int idFacturacion, CancellationToken ct)
    {
        try
        {
            await using var comando = await NuevoComandoAsync(
                "UPDATE facturaciones_de_venta SET proximo_intento_en = $2, updated_at = $3 " +
                "WHERE id_facturacion_venta = $1 AND estado = 'pendiente'::estado_facturacion_venta",
                ct, idFacturacion, reloj.Ahora + EsperaPorCorteDeLote, reloj.Ahora);
            _ = await comando.ExecuteNonQueryAsync(ct);
        }
        catch (Exception ex)
        {
            log.LogWarning(ex, "No se pudo postergar la facturación {IdFacturacion} cortada por el lote.", idFacturacion);
        }
    }

    /// <summary>Anota como intento fallido (con la espera creciente de un fallo no definitivo) una decisión cuyo procesamiento
    /// lanzó una excepción que no es uno de los resultados registrados. <paramref name="codigo"/> es un código estable, sin
    /// detalles internos. Nunca lanza.</summary>
    public async Task RegistrarFalloInesperadoAsync(int idFacturacion, string codigo, CancellationToken ct)
    {
        try
        {
            _ = await EscribirFalloAsync(idFacturacion, codigo, EsperaInicialNoDefinitiva, EsperaMaximaNoDefinitiva, ct);
        }
        catch (Exception ex)
        {
            log.LogWarning(ex, "No se pudo anotar el fallo de la facturación {IdFacturacion}.", idFacturacion);
        }
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

    /// <summary>Bloquea la fila de la decisión y devuelve su estado: la lectura única, bajo el lock, sobre la que se decide
    /// cualquier transición.</summary>
    private async Task<string?> BloquearDecisionYLeerEstadoAsync(int idFacturacion, CancellationToken ct)
    {
        await using var comando = await NuevoComandoAsync(
            "SELECT estado::text FROM facturaciones_de_venta WHERE id_facturacion_venta = $1 FOR UPDATE", ct, idFacturacion);
        return (string?)await comando.ExecuteScalarAsync(ct);
    }

    /// <summary>Mueve la decisión de pendiente a su estado nuevo. Se llama siempre con la fila bloqueada y su estado releído
    /// como pendiente (<see cref="BloquearDecisionYLeerEstadoAsync"/>).</summary>
    private async Task ActualizarDecisionAsync(
        int idFacturacion, string estado, int? idComprobanteFiscal, string? error, CancellationToken ct)
    {
        await using var comando = await NuevoComandoAsync(
            "UPDATE facturaciones_de_venta SET estado = $2::estado_facturacion_venta, id_comprobante_fiscal = $3, " +
            "ultimo_error = $4, updated_at = $5 WHERE id_facturacion_venta = $1",
            ct, idFacturacion, estado, idComprobanteFiscal, error, reloj.Ahora);
        await comando.ExecuteNonQueryAsync(ct);
    }

    /// <summary>Enlaza el comprobante adoptado a una decisión que ya no está pendiente, sin tocar su estado. Solo si no tenía
    /// enlace. Devuelve si lo enlazó.</summary>
    private async Task<bool> EnlazarComprobanteAsync(int idFacturacion, int idEspejo, CancellationToken ct)
    {
        await using var comando = await NuevoComandoAsync(
            "UPDATE facturaciones_de_venta SET id_comprobante_fiscal = $2, updated_at = $3 " +
            "WHERE id_facturacion_venta = $1 AND id_comprobante_fiscal IS NULL",
            ct, idFacturacion, idEspejo, reloj.Ahora);
        return await comando.ExecuteNonQueryAsync(ct) == 1;
    }

    // ---- El espejo ---------------------------------------------------------------------------------

    private sealed record EspejoBloqueado(string Estado, DateTimeOffset Fecha, long Numero, long Epoca);

    /// <summary>Bloquea la fila del espejo y la lee: la lectura única, bajo el lock, de su estado, su fecha y su época. Devuelve
    /// <c>null</c> si ya no existe (otra llamada lo liberó).</summary>
    private async Task<EspejoBloqueado?> BloquearYLeerEspejoAsync(int idEspejo, CancellationToken ct)
    {
        await using var comando = await NuevoComandoAsync(
            "SELECT resultado_fiscal::text, fecha, numero, xmin::text FROM comprobantes_venta " +
            "WHERE id_comprobante_venta = $1 AND id_venta_origen IS NOT NULL FOR UPDATE",
            ct, idEspejo);
        await using var lector = await comando.ExecuteReaderAsync(ct);
        if (!await lector.ReadAsync(ct))
        {
            return null;
        }

        return new EspejoBloqueado(
            lector.GetString(0), lector.GetFieldValue<DateTimeOffset>(1), lector.GetInt64(2),
            long.Parse(lector.GetString(3), CultureInfo.InvariantCulture));
    }

    /// <summary>Guarda la fecha de hoy en el espejo pendiente y devuelve su época nueva (el <c>xmin</c> de la fila reescrita).</summary>
    private async Task<long> RefrescarFechaDelEspejoAsync(int idEspejo, DateTimeOffset fecha, CancellationToken ct)
    {
        await using var comando = await NuevoComandoAsync(
            "UPDATE comprobantes_venta SET fecha = $2, updated_at = $2 WHERE id_comprobante_venta = $1 RETURNING xmin::text",
            ct, idEspejo, fecha);
        return long.Parse((string)(await comando.ExecuteScalarAsync(ct))!, CultureInfo.InvariantCulture);
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

    /// <summary>Borrado físico del espejo liberado: ARCA no lo registró, así que no queda comprobante fiscal (regla 1).
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

    /// <summary>Una fila del acumulado que falta no frena la resolución fiscal (ARCA ya contestó): se anota para loguear el error
    /// después del commit y se sigue.</summary>
    private async Task AplicarAlAcumuladoAsync(
        Salida salida, int idEmpresa, int idMedioPago, DateOnly periodo, decimal saldo, decimal vendido, decimal facturado,
        CancellationToken ct)
    {
        await using var comando = await NuevoComandoAsync(
            "UPDATE acumulados_de_facturacion SET saldo_a_facturar = saldo_a_facturar + $4, " +
            "total_vendido = total_vendido + $5, total_facturado = total_facturado + $6 " +
            "WHERE id_empresa = $1 AND id_medio_pago = $2 AND periodo = $3",
            ct, idEmpresa, idMedioPago, periodo, saldo, vendido, facturado);
        if (await comando.ExecuteNonQueryAsync(ct) == 0)
        {
            salida.AcumuladosFaltantes.Add((idMedioPago, periodo));
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
