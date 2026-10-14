using System.Data.Common;
using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Time.Testing;
using Ways.Application.Abstracciones;
using Ways.Application.Fiscal;
using Ways.Application.Usuarios;
using Ways.Application.Ventas;
using Ways.Domain.Articulos;
using Ways.Domain.Fiscal;
using Ways.Domain.Organizacion;
using Ways.Domain.Precios;
using Ways.Domain.Usuarios;
using Ways.Domain.Ventas;
using Ways.Infrastructure.Multitenancy;
using Ways.Infrastructure.Persistencia;
using Ways.Infrastructure.Seguridad;

namespace Ways.IntegrationTests;

/// <summary>
/// Facturación por medio de pago, T5c: <c>POST /api/ventas</c> intenta emitir la factura espejo antes de responder
/// (<c>EmisionEnLineaAlCobrar</c>) con los espías de WSAA/WSFE y Postgres real. Las ventas entran siempre por el endpoint, con un
/// cajero de dispositivo (que además puede reenviar con número pre-asignado), y el ticket de acceso cacheado se siembra en el
/// repositorio del host de la prueba.
///
/// Mutation-proof-tests: las cláusulas bajo prueba son que el intento solo corra con la decisión pendiente, que exija un ticket de
/// acceso ya cacheado, que su presupuesto sea min(4 s, lo que queda de los 8 s), que el barrido reciba min(3 s, lo que queda) y no
/// corra sin tiempo restante, que un corte del presupuesto no sea un intento fallido, que una excepción del intento no falle la venta
/// y que el reenvío informe la factura ya emitida. El tiempo lo maneja un <see cref="FakeTimeProvider"/> que cada test avanza
/// cuando el espía (o un interceptor de la base) confirma que se llegó al punto buscado: ninguno espera tiempo real.
/// </summary>
public partial class FacturacionDeVentasEmisionTests
{
    private const string PasswordCajeroEnLinea = "una-contraseña-de-cajero";
    private const string CookieDeDispositivo = "ways.dispositivo";

    /// <summary>Cuenta lo que el endpoint le hace a la base después de comitear la venta y lo manipula: al primer
    /// <c>SELECT</c> de certificados (la decisión) avanza el reloj de la prueba, el segundo (las compuertas del intento) puede
    /// hacerse fallar, y cada barrido (la consulta de las ventas debidas)
    /// se cuenta y, si se pidió, queda retenido hasta que su token se cancele.</summary>
    private sealed class InterceptorDelCobro(FakeTimeProvider tiempo) : DbCommandInterceptor
    {
        private int _decisiones;
        private int _barridos;

        public bool Armado { get; set; }

        public TimeSpan ConsumoDeLaDecision { get; set; }

        public bool RetenerElBarrido { get; set; }

        public bool FallarEnLasCompuertas { get; set; }

        public TaskCompletionSource Barriendo { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public int Barridos => Volatile.Read(ref _barridos);

        public override async ValueTask<InterceptionResult<DbDataReader>> ReaderExecutingAsync(
            DbCommand command, CommandEventData eventData, InterceptionResult<DbDataReader> result,
            CancellationToken cancellationToken = default)
        {
            if (Armado)
            {
                var texto = command.CommandText;
                if (texto.Contains("certificados_fiscales", StringComparison.Ordinal))
                {
                    var lectura = Interlocked.Increment(ref _decisiones);
                    if (lectura == 1)
                    {
                        tiempo.Advance(ConsumoDeLaDecision);
                    }
                    else if (lectura == 2 && FallarEnLasCompuertas)
                    {
                        throw new InvalidOperationException("Falla simulada en las compuertas del intento.");
                    }
                }

                if (texto.Contains("facturacion_decidida_en IS NULL", StringComparison.Ordinal))
                {
                    Interlocked.Increment(ref _barridos);
                    Barriendo.TrySetResult();
                    if (RetenerElBarrido)
                    {
                        await Task.Delay(Timeout.Infinite, cancellationToken);
                    }
                }
            }

            return result;
        }
    }

    private sealed record Cobro(
        Ctx C, HttpClient Cajero, FakeTimeProvider Tiempo, InterceptorDelCobro Interceptor, int IdArticulo);

    private static readonly TimeSpan Cuatro = TimeSpan.FromSeconds(4);

    /// <summary>Un tenant con punto de venta de escritorio y certificado real, un cajero de dispositivo con su bloque de números,
    /// un artículo de 100 con IVA 21 % y, con <paramref name="conTicket"/>, un ticket de acceso vigente ya cacheado.</summary>
    private async Task<Cobro> PrepararCobroAsync(string nombre, bool conTicket = true, Action<IServiceCollection>? extra = null)
    {
        var tiempo = new FakeTimeProvider(DateTimeOffset.UtcNow);
        var interceptor = new InterceptorDelCobro(tiempo);
        var c = await PrepararAsync(
            nombre, ModoPuntoVenta.Escritorio, services =>
            {
                services.AddSingleton<TimeProvider>(tiempo);
                services.AddDbContext<WaysDbContext>((_, opciones) => opciones.AddInterceptors(interceptor));
                extra?.Invoke(services);
            });

        int idArticulo;
        await using (var db = fixture.CrearContextoDeAplicacion(new TenantActualFijo(ModoDeAcceso.Tenant, c.IdTenant)))
        {
            var ahora = DateTimeOffset.UtcNow;
            var idListaGeneral = await db.ListasPrecio.Where(l => l.EsDefault).Select(l => l.Id).FirstAsync();
            var articulo = new Articulo
            {
                IdTenant = c.IdTenant, CodigoInterno = "servicio-en-linea", Nombre = "Servicio de prueba", IdArea = c.IdArea,
                IdAlicuotaIva = c.IdAlicuota21, UnidadVenta = UnidadVenta.Unidad, EsProducto = false, CreatedAt = ahora,
                UpdatedAt = ahora
            };
            db.Articulos.Add(articulo);
            await db.SaveChangesAsync();
            db.Precios.Add(new Precio
            {
                IdTenant = c.IdTenant, IdArticulo = articulo.Id, IdListaPrecio = idListaGeneral, Monto = 100m,
                VigenteDesde = ahora.AddDays(-1), VigenteHasta = null, CreatedAt = ahora, UpdatedAt = ahora
            });
            await db.SaveChangesAsync();
            idArticulo = articulo.Id;
        }

        var cajero = await LoguearCajeroDeDispositivoAsync(c);
        var reserva = await cajero.PostAsJsonAsync(
            "/api/ventas/reservas-numeracion", new SolicitudDeReservaDeNumeracion(c.IdPuntoVenta, "TX", 5));
        Assert.Equal(HttpStatusCode.OK, reserva.StatusCode);

        if (conTicket)
        {
            await GuardarTicketVigenteAsync(c);
        }

        interceptor.Armado = true;
        return new Cobro(c, cajero, tiempo, interceptor, idArticulo);
    }

    private static Task GuardarTicketVigenteAsync(Ctx c) =>
        c.Fabrica.Services.GetRequiredService<IRepositorioDeTicketDeAcceso>().GuardarAsync(
            new ClaveDeTicket(c.IdEmpresa, AmbienteFiscal.Homologacion, "wsfe"),
            new TicketDeAcceso("TOKEN_ESPIA", "SIGN_ESPIA", c.Reloj.Ahora.AddHours(12)), CancellationToken.None);

    private async Task<HttpClient> LoguearCajeroDeDispositivoAsync(Ctx c)
    {
        var alta = await c.Admin.PostAsJsonAsync(
            "/api/dispositivos", new Ways.Application.Dispositivos.AltaDispositivo(c.IdPuntoVenta, "Caja 1"));
        Assert.Equal(HttpStatusCode.Created, alta.StatusCode);

        var prefijo = $"{CookieDeDispositivo}=";
        var setCookie = Assert.Single(
            alta.Headers.GetValues("Set-Cookie"), v => v.StartsWith(prefijo, StringComparison.Ordinal));
        var valor = setCookie[prefijo.Length..];
        var cookieDispositivo = valor[..valor.IndexOf(';')];

        var hasheador = new HasheadorPbkdf2();
        await using (var db = fixture.CrearContextoDeAplicacion(TenantActualFijo.Plataforma))
        {
            var ahora = DateTimeOffset.UtcNow;
            db.Usuarios.Add(new Usuario
            {
                IdTenant = c.IdTenant, NombreUsuario = "cajero-en-linea", Mail = $"cajero-en-linea-{c.IdTenant}@ways.test",
                RolId = (int)RolConocido.Vendedor, PasswordHash = hasheador.Hashear(PasswordCajeroEnLinea),
                PasswordAlgoritmo = hasheador.Algoritmo, PasswordActualizadoEl = ahora, CreatedAt = ahora, UpdatedAt = ahora
            });
            await db.SaveChangesAsync();
        }

        var cajero = c.Fabrica.CreateClient();
        using var solicitud = new HttpRequestMessage(HttpMethod.Post, "/api/auth/login-dispositivo")
        {
            Content = JsonContent.Create(new SolicitudDeLoginDeDispositivo("cajero-en-linea", PasswordCajeroEnLinea))
        };
        solicitud.Headers.Add("Cookie", $"{CookieDeDispositivo}={cookieDispositivo}");
        var login = await cajero.SendAsync(solicitud);
        Assert.Equal(HttpStatusCode.OK, login.StatusCode);
        return cajero;
    }

    private static SolicitudDeVenta VentaDeCobro(
        Cobro t, bool forzar = true, long? numero = null, string tipo = "TX", decimal efectivo = 100m) =>
        new(t.C.IdPuntoVenta, null, tipo, null,
            [new LineaDeVenta(t.IdArticulo, 1m, null, PrecioUnitario: numero is null ? null : 100m)],
            efectivo > 0m ? [new PagoDeVenta(t.C.IdEfectivo, efectivo, null, 0m)] : [],
            null, null, NumeroPreasignado: numero, ForzarFactura: forzar && tipo == "TX");

    private static async Task<ComprobanteEmitido> VenderEnLineaAsync(HttpClient cajero, SolicitudDeVenta solicitud)
    {
        var respuesta = await cajero.PostAsJsonAsync("/api/ventas", solicitud);
        var cuerpo = await respuesta.Content.ReadAsStringAsync();
        Assert.True(respuesta.StatusCode == HttpStatusCode.Created, cuerpo);
        return JsonSerializer.Deserialize<ComprobanteEmitido>(cuerpo, OpcionesJson)!;
    }

    /// <summary>La venta en vuelo con el espía de WSFE retenido: devuelve la tarea de la venta una vez que el pedido llegó a
    /// WSFE, y solo la cancelación del pedido la deja seguir.</summary>
    private static async Task<Task<ComprobanteEmitido>> VenderConWsfeRetenidoAsync(Cobro t, SolicitudDeVenta solicitud)
    {
        var llego = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        t.C.Wsfe.SolicitarConToken = async (_, ct) =>
        {
            llego.TrySetResult();
            await Task.Delay(Timeout.Infinite, ct);
            throw new InvalidOperationException("No llega: la espera solo termina cancelada.");
        };

        var venta = VenderEnLineaAsync(t.Cajero, solicitud);
        await llego.Task.WaitAsync(Tope);
        return venta;
    }

    private async Task<(string? Cae, string? Resultado)> LeerResultadoFiscalDeLaVentaAsync(Ctx c, int idVenta)
    {
        var fila = Assert.Single(await ConsultarAsync(
            c, "SELECT cae, resultado_fiscal::text FROM comprobantes_venta WHERE id_comprobante_venta = $1",
            l => (l.IsDBNull(0) ? null : l.GetString(0), l.IsDBNull(1) ? null : l.GetString(1)), idVenta));
        return fila;
    }

    // ===================================================================================================
    // Resultados del intento
    // ===================================================================================================

    [Fact]
    public async Task UnaVentaQueSeFacturaConTicketCacheadoYArcaAprobandoRespondeAprobadoConLosDatosDelEspejo()
    {
        var t = await PrepararCobroAsync("cobroaprob");

        var emitido = await VenderEnLineaAsync(t.Cajero, VentaDeCobro(t));

        Assert.Equal(EstadosFiscalesDeVenta.Aprobado, emitido.Fiscal!.Estado);
        var factura = emitido.Fiscal.Comprobante!;
        var espejo = Assert.Single(await LeerEspejosAsync(t.C));
        Assert.Equal(emitido.Id, espejo.IdVentaOrigen);
        Assert.Equal("aprobado", espejo.Resultado);
        Assert.Equal(
            (espejo.Id, espejo.CodigoTipo, espejo.CodigoTipo[1], 1, espejo.Numero, espejo.Cae, FechaFiscalDe(espejo.Fecha)),
            (factura.IdComprobante, factura.CodigoTipoComprobante, factura.Letra, factura.PuntoDeVentaFiscal, factura.Numero,
                factura.Cae, factura.Fecha));
        Assert.Equal("70123456789012", factura.Cae);
        Assert.Equal(new DateOnly(2026, 1, 25), factura.CaeVencimiento);
        Assert.False(string.IsNullOrWhiteSpace(factura.PayloadQr));
        Assert.Empty(factura.Observaciones);

        var decision = await LeerDecisionAsync(t.C, emitido.Fiscal.IdFacturacion!.Value);
        Assert.Equal(("emitida", espejo.Id), (decision.Estado, decision.IdComprobanteFiscal));

        // Un único pedido a ARCA y ni una firma: el ticket ya estaba cacheado.
        Assert.Equal(["FECAESolicitar"], t.C.Wsfe.Operaciones);
        Assert.Equal(0, t.C.Wsaa.Solicitudes);

        // La venta es la misma: sin CAE ni resultado fiscal propios, con su pago y sus ítems.
        Assert.Equal(default, await LeerResultadoFiscalDeLaVentaAsync(t.C, emitido.Id));
        var venta = await InstantaneaAsync(t.C, emitido.Id);
        Assert.Equal(("emitido", emitido.Total, 1, 1), (venta.Estado, venta.Total, venta.Pagos, venta.Items));
    }

    [Fact]
    public async Task SinTicketCacheadoNoHayNingunaLlamadaNiEspejoNiNumeroYElLoteLaEmiteDespues()
    {
        var t = await PrepararCobroAsync("cobrosinticket", conTicket: false);

        var emitido = await VenderEnLineaAsync(t.Cajero, VentaDeCobro(t));

        Assert.Equal(EstadosFiscalesDeVenta.Pendiente, emitido.Fiscal!.Estado);
        Assert.Null(emitido.Fiscal.Error);
        Assert.Null(emitido.Fiscal.Comprobante);
        Assert.Empty(t.C.Wsfe.Operaciones);
        Assert.Equal(0, t.C.Wsaa.Solicitudes);
        Assert.Empty(await LeerEspejosAsync(t.C));
        Assert.Empty(await LeerSeriesAsync(t.C));
        var decision = await LeerDecisionAsync(t.C, emitido.Fiscal.IdFacturacion!.Value);
        Assert.Equal(("pendiente", 0, null, null), (decision.Estado, decision.Intentos, decision.UltimoError, decision.ProximoIntento));

        var lote = await ProcesarLoteAsync(t.Cajero, t.C.IdPuntoVenta);

        Assert.Equal(1, lote.Emitidas);
        Assert.Equal(1, t.C.Wsaa.Solicitudes);
        Assert.Equal(["FECAESolicitar"], t.C.Wsfe.Operaciones);
        Assert.Equal(1L, Assert.Single(await LeerEspejosAsync(t.C)).Numero);
    }

    [Fact]
    public async Task UnRechazoDeArcaRespondeRechazadoConElErrorYLaVentaNiElNumeroQuedanComoEstaban()
    {
        var t = await PrepararCobroAsync("cobrorech");
        t.C.Wsfe.Solicitar = Responde(FecaeRechazado());

        var emitido = await VenderEnLineaAsync(t.Cajero, VentaDeCobro(t));

        Assert.Equal(EstadosFiscalesDeVenta.Rechazado, emitido.Fiscal!.Estado);
        Assert.Null(emitido.Fiscal.Comprobante);
        Assert.Contains("10015: Rechazado por el receptor.", emitido.Fiscal.Error);
        Assert.Equal(EstadoComprobante.Emitido, emitido.Estado);
        Assert.Equal(default, await LeerResultadoFiscalDeLaVentaAsync(t.C, emitido.Id));
        Assert.Empty(await LeerEspejosAsync(t.C));
        Assert.Equal(1L, await LeerProximoAsync(t.C, CodigoFacturaB));
        var decision = await LeerDecisionAsync(t.C, emitido.Fiscal.IdFacturacion!.Value);
        Assert.Equal(("rechazada", emitido.Fiscal.Error), (decision.Estado, decision.UltimoError));
        Assert.Equal(1, (await InstantaneaAsync(t.C, emitido.Id)).Items);
    }

    [Fact]
    public async Task SinRespuestaDefinitivaDeArcaRespondePendienteConElErrorYAnotaElIntentoComoLoHaceElServicio()
    {
        var t = await PrepararCobroAsync("cobronodef");
        t.C.Wsfe.Solicitar = RespondeHttp500();

        var emitido = await VenderEnLineaAsync(t.Cajero, VentaDeCobro(t));

        Assert.Equal(EstadosFiscalesDeVenta.Pendiente, emitido.Fiscal!.Estado);
        Assert.StartsWith("arca_no_definitivo", emitido.Fiscal.Error, StringComparison.Ordinal);
        Assert.Null(emitido.Fiscal.Comprobante);
        var espejo = Assert.Single(await LeerEspejosAsync(t.C));
        Assert.Equal(("pendiente", 1L), (espejo.Resultado, espejo.Numero));
        var decision = await LeerDecisionAsync(t.C, emitido.Fiscal.IdFacturacion!.Value);
        Assert.Equal(("pendiente", 1), (decision.Estado, decision.Intentos));
        Assert.Equal(emitido.Fiscal.Error, decision.UltimoError);
        Assert.Equal(
            t.C.Reloj.Ahora.Add(ServicioDeFacturacionDeVentas.EsperaInicialNoDefinitiva), decision.ProximoIntento);
    }

    // ===================================================================================================
    // Presupuestos
    // ===================================================================================================

    [Fact]
    public async Task UnCorteDelPresupuestoDelIntentoRespondePendienteSinAnotarIntentoYElSiguienteLoteResuelveConsultandoPrimero()
    {
        var t = await PrepararCobroAsync("cobrocorte");
        var venta = await VenderConWsfeRetenidoAsync(t, VentaDeCobro(t));

        // Con 3,9 s del presupuesto de 4 s la venta sigue esperando a ARCA.
        t.Tiempo.Advance(Cuatro - TimeSpan.FromMilliseconds(100));
        Assert.False(venta.IsCompleted);
        t.Tiempo.Advance(TimeSpan.FromMilliseconds(100));
        var emitido = await ConTopeAsync(venta);

        Assert.Equal(EstadosFiscalesDeVenta.Pendiente, emitido.Fiscal!.Estado);
        Assert.Null(emitido.Fiscal.Error);
        Assert.Null(emitido.Fiscal.Comprobante);
        var espejo = Assert.Single(await LeerEspejosAsync(t.C));
        Assert.Equal(("pendiente", 1L), (espejo.Resultado, espejo.Numero));

        // Un corte no es un fallo: ni intento ni error ni espera, y la decisión no se posterga.
        var decision = await LeerDecisionAsync(t.C, emitido.Fiscal.IdFacturacion!.Value);
        Assert.Equal(("pendiente", 0, null, null), (decision.Estado, decision.Intentos, decision.UltimoError, decision.ProximoIntento));

        t.C.Wsfe.SolicitarConToken = null;
        t.C.Wsfe.Reiniciar();
        var lote = await ProcesarLoteAsync(t.Cajero, t.C.IdPuntoVenta);

        Assert.Equal(1, lote.Emitidas);
        Assert.Equal(["FECompConsultar", "FECAESolicitar"], t.C.Wsfe.Operaciones);
        var resuelto = Assert.Single(await LeerEspejosAsync(t.C));
        Assert.Equal(("aprobado", 1L), (resuelto.Resultado, resuelto.Numero));
    }

    [Fact]
    public async Task ElPresupuestoDelIntentoEsLoQueQuedaDelTopeCuandoLaDecisionYaGastoTiempo()
    {
        var t = await PrepararCobroAsync("cobrorestante");
        t.Interceptor.ConsumoDeLaDecision = TimeSpan.FromSeconds(5);
        var venta = await VenderConWsfeRetenidoAsync(t, VentaDeCobro(t));

        // Quedan 3 s de los 8: el intento no tiene sus 4 s.
        t.Tiempo.Advance(TimeSpan.FromMilliseconds(2900));
        Assert.False(venta.IsCompleted);
        t.Tiempo.Advance(TimeSpan.FromMilliseconds(100));
        var emitido = await ConTopeAsync(venta);

        Assert.Equal(EstadosFiscalesDeVenta.Pendiente, emitido.Fiscal!.Estado);
        Assert.Equal(1, t.C.Wsfe.Contar("FECAESolicitar"));
        Assert.Equal(0, (await LeerDecisionAsync(t.C, emitido.Fiscal.IdFacturacion!.Value)).Intentos);

        // El tope se agotó con el intento: el barrido no corrió.
        Assert.Equal(0, t.Interceptor.Barridos);
    }

    [Fact]
    public async Task ConElTopeAgotadoPorLaDecisionNiElIntentoNiElBarridoArrancan()
    {
        var t = await PrepararCobroAsync("cobroagotado");
        t.Interceptor.ConsumoDeLaDecision = TimeSpan.FromSeconds(9);

        var emitido = await VenderEnLineaAsync(t.Cajero, VentaDeCobro(t));

        Assert.Equal(EstadosFiscalesDeVenta.Pendiente, emitido.Fiscal!.Estado);
        Assert.Empty(t.C.Wsfe.Operaciones);
        Assert.Equal(0, t.C.Wsaa.Solicitudes);
        Assert.Empty(await LeerEspejosAsync(t.C));
        Assert.Empty(await LeerSeriesAsync(t.C));
        Assert.Equal(0, t.Interceptor.Barridos);
        var decision = await LeerDecisionAsync(t.C, emitido.Fiscal.IdFacturacion!.Value);
        Assert.Equal(("pendiente", 0), (decision.Estado, decision.Intentos));
    }

    [Theory]
    [InlineData(0, 3000)]
    [InlineData(6, 2000)]
    public async Task ElBarridoRecibeElMinimoEntreSusTresSegundosYLoQueQuedaDelTope(int segundosDeLaDecision, int milisegundosEsperados)
    {
        var t = await PrepararCobroAsync($"cobrobarrido{segundosDeLaDecision}", conTicket: false);
        t.Interceptor.ConsumoDeLaDecision = TimeSpan.FromSeconds(segundosDeLaDecision);
        t.Interceptor.RetenerElBarrido = true;

        var venta = VenderEnLineaAsync(t.Cajero, VentaDeCobro(t));
        await t.Interceptor.Barriendo.Task.WaitAsync(Tope);

        t.Tiempo.Advance(TimeSpan.FromMilliseconds(milisegundosEsperados - 100));
        Assert.False(venta.IsCompleted);
        t.Tiempo.Advance(TimeSpan.FromMilliseconds(100));
        var emitido = await ConTopeAsync(venta);

        // Cortar el barrido no hace fallar la venta ni se anota como fallo de nadie.
        Assert.Equal(EstadosFiscalesDeVenta.Pendiente, emitido.Fiscal!.Estado);
        Assert.Equal(1, t.Interceptor.Barridos);
        Assert.Equal(0, (await LeerDecisionAsync(t.C, emitido.Fiscal.IdFacturacion!.Value)).Intentos);
    }

    // ===================================================================================================
    // Cuándo no se intenta
    // ===================================================================================================

    [Fact]
    public async Task UnaVentaQueNoSeFacturaNoLlamaAArcaConTicketCacheadoYElBarridoCorre()
    {
        var t = await PrepararCobroAsync("cobronocorr");

        var emitido = await VenderEnLineaAsync(t.Cajero, VentaDeCobro(t, forzar: false));

        Assert.Equal(EstadosFiscalesDeVenta.NoCorresponde, emitido.Fiscal!.Estado);
        Assert.Empty(t.C.Wsfe.Operaciones);
        Assert.Equal(0, t.C.Wsaa.Solicitudes);
        Assert.Empty(await LeerEspejosAsync(t.C));
        Assert.Equal(1, t.Interceptor.Barridos);
    }

    [Fact]
    public async Task UnaVentaSinDecidirNoLlamaAArcaConTicketCacheadoNiBarre()
    {
        var t = await PrepararCobroAsync("cobrosindec");
        await using (var db = fixture.CrearContextoDeAplicacion(new TenantActualFijo(ModoDeAcceso.Tenant, t.C.IdTenant)))
        {
            var ahora = DateTimeOffset.UtcNow;
            db.Parametros.Add(new Ways.Domain.Catalogos.Parametro
            {
                IdTenant = t.C.IdTenant, IdEmpresa = t.C.IdEmpresa, Clave = Ways.Domain.Catalogos.ParametroConocido.ZonaHoraria.Clave,
                Valor = "\"Nowhere/Land\"", CreatedAt = ahora, UpdatedAt = ahora
            });
            await db.SaveChangesAsync();
        }

        var emitido = await VenderEnLineaAsync(t.Cajero, VentaDeCobro(t));

        Assert.Equal(EstadosFiscalesDeVenta.SinDecidir, emitido.Fiscal!.Estado);
        Assert.Empty(t.C.Wsfe.Operaciones);
        Assert.Equal(0, t.C.Wsaa.Solicitudes);
        Assert.Empty(await LeerEspejosAsync(t.C));
        Assert.Equal(0, t.Interceptor.Barridos);
    }

    [Fact]
    public async Task UnaDevolucionYUnaVentaDeUnaEmpresaSinCertificadoNoCambianNiLlamanAArca()
    {
        var t = await PrepararCobroAsync("cobrosincert");

        var devolucion = await VenderEnLineaAsync(t.Cajero, VentaDeCobro(t, tipo: "NCX", efectivo: 0m));
        Assert.Equal(EstadosFiscalesDeVenta.NoCorresponde, devolucion.Fiscal!.Estado);

        await EjecutarComoOwnerAsync("UPDATE certificados_fiscales SET activo = false WHERE id_empresa = $1", t.C.IdEmpresa);
        var sinCertificado = await VenderEnLineaAsync(t.Cajero, VentaDeCobro(t));
        Assert.Equal(EstadosFiscalesDeVenta.NoCorresponde, sinCertificado.Fiscal!.Estado);

        Assert.Empty(t.C.Wsfe.Operaciones);
        Assert.Equal(0, t.C.Wsaa.Solicitudes);
        Assert.Empty(await LeerEspejosAsync(t.C));
    }

    // ===================================================================================================
    // Excepciones y reenvíos
    // ===================================================================================================

    [Fact]
    public async Task UnaExcepcionDelIntentoNoFallaLaVentaYQuedaAnotadaComoIntentoFallido()
    {
        var t = await PrepararCobroAsync("cobroexc");
        t.Interceptor.FallarEnLasCompuertas = true;

        var emitido = await VenderEnLineaAsync(t.Cajero, VentaDeCobro(t));

        Assert.Equal(EstadosFiscalesDeVenta.Pendiente, emitido.Fiscal!.Estado);
        Assert.Equal(EmisionEnLineaAlCobrar.ErrorInesperado, emitido.Fiscal.Error);
        Assert.Null(emitido.Fiscal.Comprobante);
        var decision = await LeerDecisionAsync(t.C, emitido.Fiscal.IdFacturacion!.Value);
        Assert.Equal(("pendiente", 1, EmisionEnLineaAlCobrar.ErrorInesperado), (decision.Estado, decision.Intentos, decision.UltimoError));
        Assert.Empty(await LeerEspejosAsync(t.C));
        Assert.Equal(default, await LeerResultadoFiscalDeLaVentaAsync(t.C, emitido.Id));
    }

    [Fact]
    public async Task ElReenvioDeUnaVentaYaFacturadaInformaLaFacturaEmitidaSinVolverAArca()
    {
        var t = await PrepararCobroAsync("cobroreenv");
        var primera = await VenderEnLineaAsync(t.Cajero, VentaDeCobro(t, numero: 1));
        Assert.Equal(EstadosFiscalesDeVenta.Aprobado, primera.Fiscal!.Estado);
        t.C.Wsfe.Reiniciar();

        var segunda = await VenderEnLineaAsync(t.Cajero, VentaDeCobro(t, numero: 1));

        Assert.Equal(primera.Id, segunda.Id);
        Assert.Equal(EstadosFiscalesDeVenta.Aprobado, segunda.Fiscal!.Estado);
        Assert.NotNull(segunda.Fiscal.Comprobante);
        Assert.Equal(
            JsonSerializer.Serialize(primera.Fiscal.Comprobante, OpcionesJson),
            JsonSerializer.Serialize(segunda.Fiscal.Comprobante, OpcionesJson));
        Assert.Empty(t.C.Wsfe.Operaciones);
        Assert.Single(await LeerEspejosAsync(t.C));
    }

    [Fact]
    public async Task ElReenvioDeUnaVentaPendienteIntentaDeNuevoSinCrearOtroEspejoNiTomarOtroNumero()
    {
        var t = await PrepararCobroAsync("cobroreenvpend");
        t.C.Wsfe.Solicitar = RespondeHttp500();
        var primera = await VenderEnLineaAsync(t.Cajero, VentaDeCobro(t, numero: 1));
        Assert.Equal(EstadosFiscalesDeVenta.Pendiente, primera.Fiscal!.Estado);
        Assert.Equal(2L, await LeerProximoAsync(t.C, CodigoFacturaB));

        t.C.Wsfe.Solicitar = Responde(FecaeAprobado());
        t.C.Wsfe.Reiniciar();
        var segunda = await VenderEnLineaAsync(t.Cajero, VentaDeCobro(t, numero: 1));

        Assert.Equal(primera.Id, segunda.Id);
        Assert.Equal(EstadosFiscalesDeVenta.Aprobado, segunda.Fiscal!.Estado);
        Assert.Equal(1L, segunda.Fiscal.Comprobante!.Numero);
        Assert.Equal(["FECompConsultar", "FECAESolicitar"], t.C.Wsfe.Operaciones);
        Assert.Single(await LeerEspejosAsync(t.C));
        Assert.Equal(2L, await LeerProximoAsync(t.C, CodigoFacturaB));
    }

    [Fact]
    public async Task ElReenvioDeUnaDecisionYaResueltaQueConservaUnEspejoPendienteSoloInformaSuEstadoSinLlamarAArca()
    {
        var t = await PrepararCobroAsync("cobroreenvhuerf");
        t.C.Wsfe.Solicitar = RespondeHttp500();
        var primera = await VenderEnLineaAsync(t.Cajero, VentaDeCobro(t, numero: 1));
        await EjecutarComoOwnerAsync(
            "UPDATE facturaciones_de_venta SET estado = 'rechazada', ultimo_error = 'previo' WHERE id_facturacion_venta = $1",
            primera.Fiscal!.IdFacturacion!.Value);
        t.C.Wsfe.Reiniciar();

        var segunda = await VenderEnLineaAsync(t.Cajero, VentaDeCobro(t, numero: 1));

        Assert.Equal((EstadosFiscalesDeVenta.Rechazado, "previo"), (segunda.Fiscal!.Estado, segunda.Fiscal.Error));
        Assert.Empty(t.C.Wsfe.Operaciones);
        Assert.Equal("pendiente", Assert.Single(await LeerEspejosAsync(t.C)).Resultado);
    }

    [Fact]
    public async Task ElReenvioDeUnaVentaPendienteSinTicketInformaElErrorAnotadoSinNingunaLlamada()
    {
        var t = await PrepararCobroAsync("cobroreenvsint", conTicket: false);
        var primera = await VenderEnLineaAsync(t.Cajero, VentaDeCobro(t, numero: 1));
        await EjecutarComoOwnerAsync(
            "UPDATE facturaciones_de_venta SET ultimo_error = 'compuerta_de_prueba' WHERE id_facturacion_venta = $1",
            primera.Fiscal!.IdFacturacion!.Value);

        var segunda = await VenderEnLineaAsync(t.Cajero, VentaDeCobro(t, numero: 1));

        Assert.Equal(EstadosFiscalesDeVenta.Pendiente, segunda.Fiscal!.Estado);
        Assert.Equal("compuerta_de_prueba", segunda.Fiscal.Error);
        Assert.Empty(t.C.Wsfe.Operaciones);
        Assert.Equal(0, t.C.Wsaa.Solicitudes);
        Assert.Empty(await LeerEspejosAsync(t.C));
    }
}
