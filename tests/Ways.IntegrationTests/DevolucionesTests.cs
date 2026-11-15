using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Ways.Application.Abstracciones;
using Ways.Application.Organizacion;
using Ways.Application.Usuarios;
using Ways.Application.Ventas;
using Ways.Domain.Articulos;
using Ways.Domain.Catalogos;
using Ways.Domain.Clientes;
using Ways.Domain.Fiscal;
using Ways.Domain.Organizacion;
using Ways.Domain.Precios;
using Ways.Domain.Stock;
using Ways.Domain.Ventas;
using Ways.Infrastructure.Multitenancy;
using Ways.Infrastructure.Persistencia;

namespace Ways.IntegrationTests;

/// <summary>
/// Devoluciones (NCX) contra el ticket de origen, punta a punta contra Postgres real: asociado obligatorio, tope por artículo bajo el
/// lock del ticket (también con devoluciones concurrentes), importes tomados del ticket y no de la lista de hoy, bloqueo por factura
/// fiscal, guarda de anulación y lectura <c>GET /api/ventas/devolvibles</c>. Los lotes viven en <see cref="NcxLoteTests"/> y los
/// artículos por importe en <c>VentaPorImporteTests</c>.
/// </summary>
[Collection("Ways.IntegrationTests secuencial")]
public class DevolucionesTests(WaysApiFixture fixture) : IClassFixture<WaysApiFixture>
{
    private const string PasswordRoot = "root";
    private const string MailRoot = "test@test.com";

    private static readonly JsonSerializerOptions OpcionesJson = new()
    {
        PropertyNameCaseInsensitive = true,
        Converters = { new System.Text.Json.Serialization.JsonStringEnumConverter() }
    };

    private static long _numeroSembrado = 800_000;

    private sealed record Contexto(
        int IdTenant, int IdPuntoVenta, string MailAdmin, string PasswordAdmin, HttpClient Admin, int IdArea, int IdAlicuotaIva,
        int IdListaPrecio, int IdMedioEfectivo, int IdAdmin, int IdSucursal);

    private async Task<Contexto> PrepararAsync(string nombre)
    {
        using var root = fixture.CreateClient();
        var loginRoot = await root.PostAsJsonAsync("/api/auth/login", new SolicitudDeLogin(MailRoot, PasswordRoot));
        Assert.Equal(HttpStatusCode.OK, loginRoot.StatusCode);

        var mailAdmin = $"{nombre.ToLowerInvariant()}@ways.test";
        var solicitud = new SolicitudDeAprovisionamiento(nombre, $"{nombre} SA", "Local 1", mailAdmin, ModoPuntoVenta.Web);
        var respuesta = await root.PostAsJsonAsync("/api/plataforma/tenants", solicitud);
        Assert.Equal(HttpStatusCode.Created, respuesta.StatusCode);
        var resultado = (await respuesta.Content.ReadFromJsonAsync<ResultadoAprovisionamiento>())!;

        var admin = fixture.CreateClient();
        var loginAdmin = await admin.PostAsJsonAsync(
            "/api/auth/login", new SolicitudDeLogin(mailAdmin, resultado.PasswordTemporal));
        Assert.Equal(HttpStatusCode.OK, loginAdmin.StatusCode);

        await using var db = fixture.CrearContextoDeAplicacion(new TenantActualFijo(ModoDeAcceso.Tenant, resultado.IdTenant));
        var ahora = DateTimeOffset.UtcNow;

        var area = new Area { IdTenant = resultado.IdTenant, Nombre = "Dev-area", Orden = 1, CreatedAt = ahora, UpdatedAt = ahora };
        db.Areas.Add(area);
        await db.SaveChangesAsync();

        var lista = new ListaPrecio
        {
            IdTenant = resultado.IdTenant, Nombre = "Lista Dev", EsDefault = false, Modo = ModoLista.Fija, Activo = true,
            CreatedAt = ahora, UpdatedAt = ahora
        };
        db.ListasPrecio.Add(lista);
        await db.SaveChangesAsync();

        db.TurnosCaja.Add(new Ways.Domain.Caja.TurnoCaja
        {
            IdTenant = resultado.IdTenant, IdPuntoVenta = resultado.IdPuntoVenta,
            IdEmpleadoApertura = resultado.IdUsuarioAdmin, FechaApertura = ahora, FondoInicial = 0m,
            Estado = Ways.Domain.Caja.EstadoTurno.Abierto, CreatedAt = ahora, UpdatedAt = ahora
        });
        await db.SaveChangesAsync();

        return new Contexto(
            resultado.IdTenant, resultado.IdPuntoVenta, mailAdmin, resultado.PasswordTemporal, admin, area.Id,
            await db.AlicuotasIva.Select(a => a.Id).FirstAsync(), lista.Id,
            await db.MediosPago.Where(m => m.Comportamiento == ComportamientoMedioPago.Efectivo).Select(m => m.Id).FirstAsync(),
            resultado.IdUsuarioAdmin, await SucursalDePrueba.DeAsync(db, resultado.IdPuntoVenta));
    }

    private async Task<int> SembrarArticuloAsync(Contexto ctx, string nombre, decimal precio)
    {
        await using var db = fixture.CrearContextoDeAplicacion(new TenantActualFijo(ModoDeAcceso.Tenant, ctx.IdTenant));
        var ahora = DateTimeOffset.UtcNow;
        var articulo = new Articulo
        {
            IdTenant = ctx.IdTenant, CodigoInterno = $"{nombre}-{Guid.NewGuid():N}", Nombre = nombre, IdArea = ctx.IdArea,
            IdAlicuotaIva = ctx.IdAlicuotaIva, UnidadVenta = UnidadVenta.Unidad, EsProducto = true, CreatedAt = ahora, UpdatedAt = ahora
        };
        db.Articulos.Add(articulo);
        await db.SaveChangesAsync();

        db.Precios.Add(new Precio
        {
            IdTenant = ctx.IdTenant, IdArticulo = articulo.Id, IdListaPrecio = ctx.IdListaPrecio, Monto = precio,
            VigenteDesde = ahora.AddDays(-1), VigenteHasta = null, CreatedAt = ahora, UpdatedAt = ahora
        });
        await db.SaveChangesAsync();
        return articulo.Id;
    }

    private async Task<int> SembrarClienteAsync(Contexto ctx, string nombre)
    {
        await using var db = fixture.CrearContextoDeAplicacion(new TenantActualFijo(ModoDeAcceso.Tenant, ctx.IdTenant));
        var ahora = DateTimeOffset.UtcNow;
        var cliente = new Cliente
        {
            IdTenant = ctx.IdTenant, Numero = 1000 + Random.Shared.Next(1, 100_000), Nombre = nombre,
            IdCondicionFiscal = await db.CondicionesFiscales.Select(c => c.Id).FirstAsync(), IdListaPrecio = ctx.IdListaPrecio,
            LimiteCredito = 0m, CreditoIlimitado = false, Activo = true, CreatedAt = ahora, UpdatedAt = ahora
        };
        db.Clientes.Add(cliente);
        await db.SaveChangesAsync();
        return cliente.Id;
    }

    private static async Task<ComprobanteEmitido> VenderAsync(
        Contexto ctx, int idCliente, decimal total, params LineaDeVenta[] lineas)
    {
        var respuesta = await ctx.Admin.PostAsJsonAsync("/api/ventas", new SolicitudDeVenta(
            ctx.IdPuntoVenta, idCliente, "TX", null, lineas, [new PagoDeVenta(ctx.IdMedioEfectivo, total, null, 0m)], null, null));
        var cuerpo = await respuesta.Content.ReadAsStringAsync();
        Assert.True(respuesta.StatusCode == HttpStatusCode.Created, cuerpo);
        return JsonSerializer.Deserialize<ComprobanteEmitido>(cuerpo, OpcionesJson)!;
    }

    private static Task<HttpResponseMessage> DevolverAsync(
        HttpClient cliente, Contexto ctx, int idCliente, int? idAsociado, params LineaDeVenta[] lineas) =>
        cliente.PostAsJsonAsync("/api/ventas", new SolicitudDeVenta(
            ctx.IdPuntoVenta, idCliente, "NCX", idAsociado, lineas, [], null, null));

    private static async Task<ComprobanteEmitido> DevolverBienAsync(
        Contexto ctx, int idCliente, int idAsociado, params LineaDeVenta[] lineas)
    {
        var respuesta = await DevolverAsync(ctx.Admin, ctx, idCliente, idAsociado, lineas);
        var cuerpo = await respuesta.Content.ReadAsStringAsync();
        Assert.True(respuesta.StatusCode == HttpStatusCode.Created, cuerpo);
        return JsonSerializer.Deserialize<ComprobanteEmitido>(cuerpo, OpcionesJson)!;
    }

    private static async Task AfirmarErrorAsync(HttpResponseMessage respuesta, HttpStatusCode estado, string codigo)
    {
        var cuerpo = await respuesta.Content.ReadAsStringAsync();
        Assert.True(respuesta.StatusCode == estado, cuerpo);
        Assert.Equal(codigo, JsonDocument.Parse(cuerpo).RootElement.GetProperty("codigo").GetString());
    }

    private async Task<int> ContarNcxAsync(Contexto ctx, int idAsociado)
    {
        await using var db = fixture.CrearContextoDeAplicacion(new TenantActualFijo(ModoDeAcceso.Tenant, ctx.IdTenant));
        return await db.ComprobantesVenta.CountAsync(c => c.IdComprobanteAsociado == idAsociado);
    }

    private async Task<decimal> StockAsync(Contexto ctx, int idArticulo)
    {
        await using var db = fixture.CrearContextoDeAplicacion(new TenantActualFijo(ModoDeAcceso.Tenant, ctx.IdTenant));
        return await db.Stock.Where(s => s.IdArticulo == idArticulo && s.IdSucursal == ctx.IdSucursal)
            .Select(s => s.Cantidad).FirstOrDefaultAsync();
    }

    // ---- Asociado obligatorio ----------------------------------------------------------------------

    [Fact]
    public async Task UnaDevolucionSinTicketDeOrigenSeRechazaYNoPersisteNada()
    {
        var ctx = await PrepararAsync(nameof(UnaDevolucionSinTicketDeOrigenSeRechazaYNoPersisteNada));
        var idArticulo = await SembrarArticuloAsync(ctx, "dev-sin-origen", 100m);
        var idCliente = await SembrarClienteAsync(ctx, "Cliente Sin Origen");

        await AfirmarErrorAsync(
            await DevolverAsync(ctx.Admin, ctx, idCliente, null, new LineaDeVenta(idArticulo, 1m, null)),
            HttpStatusCode.BadRequest, "comprobante_asociado_requerido");

        await using var db = fixture.CrearContextoDeAplicacion(new TenantActualFijo(ModoDeAcceso.Tenant, ctx.IdTenant));
        Assert.Equal(0, await db.ComprobantesVenta.CountAsync(c => c.Total < 0m));
        Assert.Equal(0m, await StockAsync(ctx, idArticulo));
    }

    [Fact]
    public async Task UnaDevolucionNoSeAsociaAOtraDevolucion()
    {
        var ctx = await PrepararAsync(nameof(UnaDevolucionNoSeAsociaAOtraDevolucion));
        var idArticulo = await SembrarArticuloAsync(ctx, "dev-sobre-dev", 100m);
        var idCliente = await SembrarClienteAsync(ctx, "Cliente Sobre Dev");
        var venta = await VenderAsync(ctx, idCliente, 200m, new LineaDeVenta(idArticulo, 2m, null));
        var devolucion = await DevolverBienAsync(ctx, idCliente, venta.Id, new LineaDeVenta(idArticulo, 1m, null));

        await AfirmarErrorAsync(
            await DevolverAsync(ctx.Admin, ctx, idCliente, devolucion.Id, new LineaDeVenta(idArticulo, 1m, null)),
            HttpStatusCode.BadRequest, "comprobante_asociado_invalido");
    }

    // ---- Tope por artículo --------------------------------------------------------------------------

    [Fact]
    public async Task NoSePuedeDevolverMasDeLoVendidoMenosLoYaDevuelto()
    {
        var ctx = await PrepararAsync(nameof(NoSePuedeDevolverMasDeLoVendidoMenosLoYaDevuelto));
        var idArticulo = await SembrarArticuloAsync(ctx, "dev-tope", 100m);
        var idCliente = await SembrarClienteAsync(ctx, "Cliente Tope");
        var venta = await VenderAsync(ctx, idCliente, 300m, new LineaDeVenta(idArticulo, 3m, null));

        await DevolverBienAsync(ctx, idCliente, venta.Id, new LineaDeVenta(idArticulo, 2m, null));

        await AfirmarErrorAsync(
            await DevolverAsync(ctx.Admin, ctx, idCliente, venta.Id, new LineaDeVenta(idArticulo, 2m, null)),
            HttpStatusCode.Conflict, "devolucion_excede_lo_vendido");
        Assert.Equal(1, await ContarNcxAsync(ctx, venta.Id));

        await DevolverBienAsync(ctx, idCliente, venta.Id, new LineaDeVenta(idArticulo, 1m, null));
        Assert.Equal(2, await ContarNcxAsync(ctx, venta.Id));

        await AfirmarErrorAsync(
            await DevolverAsync(ctx.Admin, ctx, idCliente, venta.Id, new LineaDeVenta(idArticulo, 1m, null)),
            HttpStatusCode.Conflict, "devolucion_excede_lo_vendido");

        // Stock: -3 de la venta + 2 + 1 de las devoluciones emitidas; la rechazada no movió nada.
        Assert.Equal(0m, await StockAsync(ctx, idArticulo));
    }

    [Fact]
    public async Task UnArticuloQueElTicketNoVendioSeRechazaConElMismoCodigo()
    {
        var ctx = await PrepararAsync(nameof(UnArticuloQueElTicketNoVendioSeRechazaConElMismoCodigo));
        var vendido = await SembrarArticuloAsync(ctx, "dev-vendido", 100m);
        var otro = await SembrarArticuloAsync(ctx, "dev-otro", 100m);
        var idCliente = await SembrarClienteAsync(ctx, "Cliente Otro Articulo");
        var venta = await VenderAsync(ctx, idCliente, 100m, new LineaDeVenta(vendido, 1m, null));

        await AfirmarErrorAsync(
            await DevolverAsync(ctx.Admin, ctx, idCliente, venta.Id, new LineaDeVenta(otro, 1m, null)),
            HttpStatusCode.Conflict, "devolucion_excede_lo_vendido");
    }

    [Fact]
    public async Task DosLineasDelMismoArticuloEnUnaMismaDevolucionSumanContraElTope()
    {
        var ctx = await PrepararAsync(nameof(DosLineasDelMismoArticuloEnUnaMismaDevolucionSumanContraElTope));
        var idArticulo = await SembrarArticuloAsync(ctx, "dev-dos-lineas", 100m);
        var idCliente = await SembrarClienteAsync(ctx, "Cliente Dos Lineas");
        var venta = await VenderAsync(ctx, idCliente, 200m, new LineaDeVenta(idArticulo, 2m, null));

        await AfirmarErrorAsync(
            await DevolverAsync(
                ctx.Admin, ctx, idCliente, venta.Id, new LineaDeVenta(idArticulo, 2m, null), new LineaDeVenta(idArticulo, 1m, null)),
            HttpStatusCode.Conflict, "devolucion_excede_lo_vendido");
    }

    [Fact]
    public async Task AnularUnaDevolucionLiberaSusUnidadesParaElTope()
    {
        var ctx = await PrepararAsync(nameof(AnularUnaDevolucionLiberaSusUnidadesParaElTope));
        var idArticulo = await SembrarArticuloAsync(ctx, "dev-libera", 100m);
        var idCliente = await SembrarClienteAsync(ctx, "Cliente Libera");
        var venta = await VenderAsync(ctx, idCliente, 200m, new LineaDeVenta(idArticulo, 2m, null));

        var primera = await DevolverBienAsync(ctx, idCliente, venta.Id, new LineaDeVenta(idArticulo, 2m, null));
        await AfirmarErrorAsync(
            await DevolverAsync(ctx.Admin, ctx, idCliente, venta.Id, new LineaDeVenta(idArticulo, 1m, null)),
            HttpStatusCode.Conflict, "devolucion_excede_lo_vendido");

        var anulacion = await ctx.Admin.PostAsync($"/api/ventas/{primera.Id}/anulacion", null);
        Assert.Equal(HttpStatusCode.OK, anulacion.StatusCode);

        await DevolverBienAsync(ctx, idCliente, venta.Id, new LineaDeVenta(idArticulo, 2m, null));
    }

    // ---- Importe: lo que se cobró -------------------------------------------------------------------

    [Fact]
    public async Task LaDevolucionAcreditaLoCobradoEnElTicketAunqueLaListaHayaCambiado()
    {
        var ctx = await PrepararAsync(nameof(LaDevolucionAcreditaLoCobradoEnElTicketAunqueLaListaHayaCambiado));
        var idArticulo = await SembrarArticuloAsync(ctx, "dev-precio", 100m);
        var idCliente = await SembrarClienteAsync(ctx, "Cliente Precio");
        var venta = await VenderAsync(ctx, idCliente, 200m, new LineaDeVenta(idArticulo, 2m, null));

        await using (var db = fixture.CrearContextoDeAplicacion(new TenantActualFijo(ModoDeAcceso.Tenant, ctx.IdTenant)))
        {
            var precio = await db.Precios.FirstAsync(p => p.IdArticulo == idArticulo);
            precio.Monto = 150m;
            await db.SaveChangesAsync();
        }

        var devolucion = await DevolverBienAsync(ctx, idCliente, venta.Id, new LineaDeVenta(idArticulo, 1m, null));

        Assert.Equal(-100m, devolucion.Total);
        var item = Assert.Single(devolucion.Items);
        Assert.Equal(-1m, item.Cantidad);
        Assert.Equal(100m, item.PrecioUnitario);
        Assert.Equal(-100m, item.Total);
    }

    [Fact]
    public async Task LaDevolucionAcreditaElNetoDelAjusteManualQueTuvoLaLineaVendida()
    {
        var ctx = await PrepararAsync(nameof(LaDevolucionAcreditaElNetoDelAjusteManualQueTuvoLaLineaVendida));
        var idArticulo = await SembrarArticuloAsync(ctx, "dev-ajuste", 100m);
        var idCliente = await SembrarClienteAsync(ctx, "Cliente Ajuste");
        var venta = await VenderAsync(ctx, idCliente, 180m, new LineaDeVenta(idArticulo, 2m, null, AjusteManualPorcentaje: -10m));
        Assert.Equal(180m, venta.Total);

        var primera = await DevolverBienAsync(ctx, idCliente, venta.Id, new LineaDeVenta(idArticulo, 1m, null));
        var segunda = await DevolverBienAsync(ctx, idCliente, venta.Id, new LineaDeVenta(idArticulo, 1m, null));

        Assert.Equal(-90m, primera.Total);
        Assert.Equal(-90m, segunda.Total);
        Assert.Equal(-180m, primera.Total + segunda.Total);
        Assert.Null(Assert.Single(primera.Items).AjusteManualPorcentaje);
    }

    [Fact]
    public async Task VariasDevolucionesParcialesAcreditanExactamenteElTotalDelItemSinPerderPesos()
    {
        var ctx = await PrepararAsync(nameof(VariasDevolucionesParcialesAcreditanExactamenteElTotalDelItemSinPerderPesos));
        var idArticulo = await SembrarArticuloAsync(ctx, "dev-redondeo", 33.34m);
        var idCliente = await SembrarClienteAsync(ctx, "Cliente Redondeo");
        var venta = await VenderAsync(ctx, idCliente, 100m, new LineaDeVenta(idArticulo, 3m, null));
        Assert.Equal(100m, venta.Total);

        var creditos = new List<decimal>();
        for (var i = 0; i < 3; i++)
        {
            creditos.Add((await DevolverBienAsync(ctx, idCliente, venta.Id, new LineaDeVenta(idArticulo, 1m, null))).Total);
        }

        Assert.Equal([-33m, -34m, -33m], creditos);
        Assert.Equal(-100m, creditos.Sum());
    }

    [Fact]
    public async Task UnaLineaDeDevolucionNoAdmiteAjusteManual()
    {
        var ctx = await PrepararAsync(nameof(UnaLineaDeDevolucionNoAdmiteAjusteManual));
        var idArticulo = await SembrarArticuloAsync(ctx, "dev-sin-ajuste", 100m);
        var idCliente = await SembrarClienteAsync(ctx, "Cliente Sin Ajuste");
        var venta = await VenderAsync(ctx, idCliente, 100m, new LineaDeVenta(idArticulo, 1m, null));

        await AfirmarErrorAsync(
            await DevolverAsync(
                ctx.Admin, ctx, idCliente, venta.Id, new LineaDeVenta(idArticulo, 1m, null, AjusteManualPorcentaje: -50m)),
            HttpStatusCode.BadRequest, "ajuste_manual_no_admitido_en_devolucion");
    }

    // ---- Concurrencia: el tope se juzga bajo el lock del ticket -------------------------------------

    [Fact]
    public async Task DosDevolucionesQueSeSuperponenNoDevuelvenMasDeLoVendido()
    {
        var ctx = await PrepararAsync(nameof(DosDevolucionesQueSeSuperponenNoDevuelvenMasDeLoVendido));
        var idArticulo = await SembrarArticuloAsync(ctx, "dev-carrera", 100m);
        var idCliente = await SembrarClienteAsync(ctx, "Cliente Carrera");
        var venta = await VenderAsync(ctx, idCliente, 300m, new LineaDeVenta(idArticulo, 3m, null));

        // La perdedora ya armó su plan contra el ticket SIN devoluciones y se pausa al abrir su primera transacción (la de
        // numeración, antes de la que escribe); la ganadora corre completa y comitea. Al retomar, la perdedora solo puede
        // rechazarse si re-juzga lo ya devuelto DESPUÉS del lock del ticket.
        var transaccionIniciada = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var puedeContinuar = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var interceptor = new InterceptorDePausaTrasIniciarLaTransaccion(transaccionIniciada, puedeContinuar);

        await using var factory = fixture.WithWebHostBuilder(builder =>
            builder.ConfigureServices(services =>
                services.AddDbContext<WaysDbContext>((_, options) => options.AddInterceptors(interceptor))));

        using var perdedor = factory.CreateClient();
        var login = await perdedor.PostAsJsonAsync("/api/auth/login", new SolicitudDeLogin(ctx.MailAdmin, ctx.PasswordAdmin));
        Assert.Equal(HttpStatusCode.OK, login.StatusCode);

        var tareaPerdedora = DevolverAsync(perdedor, ctx, idCliente, venta.Id, new LineaDeVenta(idArticulo, 3m, null));
        await transaccionIniciada.Task;

        var ganadora = await DevolverBienAsync(ctx, idCliente, venta.Id, new LineaDeVenta(idArticulo, 3m, null));
        Assert.Equal(-300m, ganadora.Total);

        puedeContinuar.TrySetResult();
        await AfirmarErrorAsync(await tareaPerdedora, HttpStatusCode.Conflict, "devolucion_excede_lo_vendido");

        Assert.Equal(1, await ContarNcxAsync(ctx, venta.Id));
        Assert.Equal(0m, await StockAsync(ctx, idArticulo));
    }

    [Fact]
    public async Task DosDevolucionesParcialesSimultaneasDelMismoTicketComitean()
    {
        var ctx = await PrepararAsync(nameof(DosDevolucionesParcialesSimultaneasDelMismoTicketComitean));
        var idArticulo = await SembrarArticuloAsync(ctx, "dev-parciales", 100m);
        var idCliente = await SembrarClienteAsync(ctx, "Cliente Parciales");
        var venta = await VenderAsync(ctx, idCliente, 400m, new LineaDeVenta(idArticulo, 4m, null));

        var respuestas = await Task.WhenAll(
            DevolverAsync(ctx.Admin, ctx, idCliente, venta.Id, new LineaDeVenta(idArticulo, 2m, null)),
            DevolverAsync(ctx.Admin, ctx, idCliente, venta.Id, new LineaDeVenta(idArticulo, 2m, null)));

        // Entre las dos devuelven exactamente lo vendido: ninguna se pierde ni se pasa, y no hay 500.
        Assert.All(respuestas, r => Assert.True(
            r.StatusCode is HttpStatusCode.Created or HttpStatusCode.Conflict, r.StatusCode.ToString()));
        Assert.Contains(respuestas, r => r.StatusCode == HttpStatusCode.Created);

        var emitidas = await ContarNcxAsync(ctx, venta.Id);
        Assert.Equal(respuestas.Count(r => r.StatusCode == HttpStatusCode.Created), emitidas);
        Assert.Equal(-4m + (2m * emitidas), await StockAsync(ctx, idArticulo));
    }

    // ---- Factura fiscal ----------------------------------------------------------------------------

    private async Task SembrarEspejoAsync(Contexto ctx, int idVenta, int idCliente, ResultadoFiscal resultado)
    {
        await using var db = fixture.CrearContextoDeAplicacion(new TenantActualFijo(ModoDeAcceso.Tenant, ctx.IdTenant));
        var ahora = DateTimeOffset.UtcNow;
        var aprobado = resultado is ResultadoFiscal.Aprobado or ResultadoFiscal.AprobadoConObservaciones;
        db.ComprobantesVenta.Add(new ComprobanteVenta
        {
            IdTenant = ctx.IdTenant,
            IdTipoComprobante = await db.TiposComprobante.Where(t => t.Codigo == "FB").Select(t => t.Id).FirstAsync(),
            Numero = Interlocked.Increment(ref _numeroSembrado), Fecha = ahora, IdPuntoVenta = ctx.IdPuntoVenta,
            IdEmpleado = ctx.IdAdmin, IdCliente = idCliente, Subtotal = 100m, Total = 100m, Estado = EstadoComprobante.Emitido,
            IdVentaOrigen = idVenta, ResultadoFiscal = resultado, Cae = aprobado ? "12345678901234" : null,
            CaeVencimiento = aprobado ? new DateOnly(2027, 5, 20) : null, CreatedAt = ahora, UpdatedAt = ahora
        });
        await db.SaveChangesAsync();
    }

    [Theory]
    [InlineData(ResultadoFiscal.Aprobado)]
    [InlineData(ResultadoFiscal.AprobadoConObservaciones)]
    [InlineData(ResultadoFiscal.Pendiente)]
    public async Task UnTicketConFacturaAprobadaOEnTramiteNoSePuedeDevolver(ResultadoFiscal resultado)
    {
        var ctx = await PrepararAsync($"{nameof(UnTicketConFacturaAprobadaOEnTramiteNoSePuedeDevolver)}{resultado}");
        var idArticulo = await SembrarArticuloAsync(ctx, "dev-cae", 100m);
        var idCliente = await SembrarClienteAsync(ctx, "Cliente Cae");
        var venta = await VenderAsync(ctx, idCliente, 100m, new LineaDeVenta(idArticulo, 1m, null));
        await SembrarEspejoAsync(ctx, venta.Id, idCliente, resultado);

        var respuesta = await DevolverAsync(ctx.Admin, ctx, idCliente, venta.Id, new LineaDeVenta(idArticulo, 1m, null));

        await AfirmarErrorAsync(respuesta, HttpStatusCode.Conflict, "devolucion_requiere_nota_de_credito_fiscal");
        Assert.Contains("nota de crédito fiscal", await respuesta.Content.ReadAsStringAsync());
        Assert.Equal(0, await ContarNcxAsync(ctx, venta.Id));
    }

    [Fact]
    public async Task UnTicketConLaFacturaRechazadaSiSePuedeDevolver()
    {
        var ctx = await PrepararAsync(nameof(UnTicketConLaFacturaRechazadaSiSePuedeDevolver));
        var idArticulo = await SembrarArticuloAsync(ctx, "dev-rechazada", 100m);
        var idCliente = await SembrarClienteAsync(ctx, "Cliente Rechazada");
        var venta = await VenderAsync(ctx, idCliente, 100m, new LineaDeVenta(idArticulo, 1m, null));
        await SembrarEspejoAsync(ctx, venta.Id, idCliente, ResultadoFiscal.Rechazado);

        await DevolverBienAsync(ctx, idCliente, venta.Id, new LineaDeVenta(idArticulo, 1m, null));
    }

    [Fact]
    public async Task UnaFacturaEspejoNoSeAceptaComoTicketDeOrigen()
    {
        var ctx = await PrepararAsync(nameof(UnaFacturaEspejoNoSeAceptaComoTicketDeOrigen));
        var idArticulo = await SembrarArticuloAsync(ctx, "dev-espejo", 100m);
        var idCliente = await SembrarClienteAsync(ctx, "Cliente Espejo");
        var venta = await VenderAsync(ctx, idCliente, 100m, new LineaDeVenta(idArticulo, 1m, null));
        await SembrarEspejoAsync(ctx, venta.Id, idCliente, ResultadoFiscal.Aprobado);

        int idEspejo;
        await using (var db = fixture.CrearContextoDeAplicacion(new TenantActualFijo(ModoDeAcceso.Tenant, ctx.IdTenant)))
        {
            idEspejo = await db.ComprobantesVenta.Where(c => c.IdVentaOrigen == venta.Id).Select(c => c.Id).SingleAsync();
        }

        await AfirmarErrorAsync(
            await DevolverAsync(ctx.Admin, ctx, idCliente, idEspejo, new LineaDeVenta(idArticulo, 1m, null)),
            HttpStatusCode.BadRequest, "comprobante_asociado_es_espejo_fiscal");
    }

    // ---- Anulación del ticket ----------------------------------------------------------------------

    [Fact]
    public async Task UnTicketConDevolucionesEmitidasNoSeAnula()
    {
        var ctx = await PrepararAsync(nameof(UnTicketConDevolucionesEmitidasNoSeAnula));
        var idArticulo = await SembrarArticuloAsync(ctx, "dev-anular", 100m);
        var idCliente = await SembrarClienteAsync(ctx, "Cliente Anular");
        var venta = await VenderAsync(ctx, idCliente, 200m, new LineaDeVenta(idArticulo, 2m, null));
        var devolucion = await DevolverBienAsync(ctx, idCliente, venta.Id, new LineaDeVenta(idArticulo, 1m, null));

        await AfirmarErrorAsync(
            await ctx.Admin.PostAsync($"/api/ventas/{venta.Id}/anulacion", null), HttpStatusCode.Conflict, "venta_con_devoluciones");

        await using (var db = fixture.CrearContextoDeAplicacion(new TenantActualFijo(ModoDeAcceso.Tenant, ctx.IdTenant)))
        {
            Assert.Equal(EstadoComprobante.Emitido, await db.ComprobantesVenta.Where(c => c.Id == venta.Id).Select(c => c.Estado).SingleAsync());
        }

        // Anulada la devolución, el ticket vuelve a poder anularse.
        Assert.Equal(HttpStatusCode.OK, (await ctx.Admin.PostAsync($"/api/ventas/{devolucion.Id}/anulacion", null)).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await ctx.Admin.PostAsync($"/api/ventas/{venta.Id}/anulacion", null)).StatusCode);
    }

    [Fact]
    public async Task UnTicketAnuladoNoSePuedeDevolver()
    {
        var ctx = await PrepararAsync(nameof(UnTicketAnuladoNoSePuedeDevolver));
        var idArticulo = await SembrarArticuloAsync(ctx, "dev-anulado", 100m);
        var idCliente = await SembrarClienteAsync(ctx, "Cliente Anulado");
        var venta = await VenderAsync(ctx, idCliente, 100m, new LineaDeVenta(idArticulo, 1m, null));
        Assert.Equal(HttpStatusCode.OK, (await ctx.Admin.PostAsync($"/api/ventas/{venta.Id}/anulacion", null)).StatusCode);

        await AfirmarErrorAsync(
            await DevolverAsync(ctx.Admin, ctx, idCliente, venta.Id, new LineaDeVenta(idArticulo, 1m, null)),
            HttpStatusCode.BadRequest, "comprobante_asociado_invalido");
    }

    // ---- GET /api/ventas/devolvibles ---------------------------------------------------------------

    private static async Task<TicketDevolvible> LeerDevolviblesAsync(HttpResponseMessage respuesta)
    {
        var cuerpo = await respuesta.Content.ReadAsStringAsync();
        Assert.True(respuesta.StatusCode == HttpStatusCode.OK, cuerpo);
        return JsonSerializer.Deserialize<TicketDevolvible>(cuerpo, OpcionesJson)!;
    }

    [Fact]
    public async Task LaLecturaDevolviblesInformaLoVendidoLoDevueltoYLoQueQueda()
    {
        var ctx = await PrepararAsync(nameof(LaLecturaDevolviblesInformaLoVendidoLoDevueltoYLoQueQueda));
        var pancho = await SembrarArticuloAsync(ctx, "dev-lectura-pancho", 100m);
        var coca = await SembrarArticuloAsync(ctx, "dev-lectura-coca", 50m);
        var idCliente = await SembrarClienteAsync(ctx, "Cliente Lectura");
        var venta = await VenderAsync(
            ctx, idCliente, 350m, new LineaDeVenta(pancho, 3m, null), new LineaDeVenta(coca, 1m, null));
        await DevolverBienAsync(ctx, idCliente, venta.Id, new LineaDeVenta(pancho, 1m, null));

        var porNumero = await LeerDevolviblesAsync(await ctx.Admin.GetAsync(
            $"/api/ventas/devolvibles?idPuntoVenta={ctx.IdPuntoVenta}&numero={venta.Numero}"));
        var porId = await LeerDevolviblesAsync(await ctx.Admin.GetAsync($"/api/ventas/{venta.Id}/devolvibles"));

        Assert.Equal(porNumero.Id, porId.Id);
        Assert.Equal(porNumero.Lineas, porId.Lineas);
        Assert.Equal(venta.Id, porNumero.Id);
        Assert.Equal(venta.NumeroVisible, porNumero.NumeroVisible);
        Assert.Equal(idCliente, porNumero.IdCliente);
        Assert.Equal("Cliente Lectura", porNumero.NombreCliente);
        Assert.Equal(350m, porNumero.Total);
        Assert.True(porNumero.Devolvible);
        Assert.Null(porNumero.Motivo);

        var lineaPancho = Assert.Single(porNumero.Lineas, l => l.IdArticulo == pancho);
        Assert.Equal(3m, lineaPancho.CantidadVendida);
        Assert.Equal(1m, lineaPancho.CantidadDevuelta);
        Assert.Equal(2m, lineaPancho.CantidadDevolvible);
        Assert.Equal(100m, lineaPancho.ImporteUnitario);
        Assert.Equal(200m, lineaPancho.ImporteDeLoRestante);
        Assert.Null(lineaPancho.Importe);

        var lineaCoca = Assert.Single(porNumero.Lineas, l => l.IdArticulo == coca);
        Assert.Equal(1m, lineaCoca.CantidadDevolvible);
        Assert.Equal(50m, lineaCoca.ImporteDeLoRestante);
    }

    [Fact]
    public async Task LaLecturaDevolviblesDiceElMotivoSiElTicketNoSePuedeDevolver()
    {
        var ctx = await PrepararAsync(nameof(LaLecturaDevolviblesDiceElMotivoSiElTicketNoSePuedeDevolver));
        var idArticulo = await SembrarArticuloAsync(ctx, "dev-motivos", 100m);
        var idCliente = await SembrarClienteAsync(ctx, "Cliente Motivos");

        var agotado = await VenderAsync(ctx, idCliente, 100m, new LineaDeVenta(idArticulo, 1m, null));
        await DevolverBienAsync(ctx, idCliente, agotado.Id, new LineaDeVenta(idArticulo, 1m, null));
        var anulado = await VenderAsync(ctx, idCliente, 100m, new LineaDeVenta(idArticulo, 1m, null));
        await ctx.Admin.PostAsync($"/api/ventas/{anulado.Id}/anulacion", null);
        var conCae = await VenderAsync(ctx, idCliente, 100m, new LineaDeVenta(idArticulo, 1m, null));
        await SembrarEspejoAsync(ctx, conCae.Id, idCliente, ResultadoFiscal.Aprobado);
        var devolucion = await DevolverBienAsync(ctx, idCliente, (await VenderAsync(
            ctx, idCliente, 100m, new LineaDeVenta(idArticulo, 1m, null))).Id, new LineaDeVenta(idArticulo, 1m, null));

        var delAgotado = await LeerDevolviblesAsync(await ctx.Admin.GetAsync($"/api/ventas/{agotado.Id}/devolvibles"));
        Assert.False(delAgotado.Devolvible);
        Assert.Equal("sin_unidades_por_devolver", delAgotado.Motivo);
        Assert.Equal(0m, Assert.Single(delAgotado.Lineas).CantidadDevolvible);

        var delAnulado = await LeerDevolviblesAsync(await ctx.Admin.GetAsync($"/api/ventas/{anulado.Id}/devolvibles"));
        Assert.False(delAnulado.Devolvible);
        Assert.Equal("anulado", delAnulado.Motivo);
        Assert.Equal(1m, Assert.Single(delAnulado.Lineas).CantidadDevolvible);

        var delConCae = await LeerDevolviblesAsync(await ctx.Admin.GetAsync($"/api/ventas/{conCae.Id}/devolvibles"));
        Assert.False(delConCae.Devolvible);
        Assert.Equal("con_factura_fiscal", delConCae.Motivo);

        var deUnaNota = await LeerDevolviblesAsync(await ctx.Admin.GetAsync($"/api/ventas/{devolucion.Id}/devolvibles"));
        Assert.False(deUnaNota.Devolvible);
        Assert.Equal("no_es_un_ticket_de_venta", deUnaNota.Motivo);
    }

    [Fact]
    public async Task LaLecturaDevolviblesDaNotFoundParaUnNumeroInexistenteUnEspejoYOtroTenant()
    {
        var ctx = await PrepararAsync(nameof(LaLecturaDevolviblesDaNotFoundParaUnNumeroInexistenteUnEspejoYOtroTenant));
        var otro = await PrepararAsync(nameof(LaLecturaDevolviblesDaNotFoundParaUnNumeroInexistenteUnEspejoYOtroTenant) + "Otro");
        var idArticulo = await SembrarArticuloAsync(ctx, "dev-404", 100m);
        var idCliente = await SembrarClienteAsync(ctx, "Cliente 404");
        var venta = await VenderAsync(ctx, idCliente, 100m, new LineaDeVenta(idArticulo, 1m, null));
        await SembrarEspejoAsync(ctx, venta.Id, idCliente, ResultadoFiscal.Aprobado);
        int idEspejo;
        await using (var db = fixture.CrearContextoDeAplicacion(new TenantActualFijo(ModoDeAcceso.Tenant, ctx.IdTenant)))
        {
            idEspejo = await db.ComprobantesVenta.Where(c => c.IdVentaOrigen == venta.Id).Select(c => c.Id).SingleAsync();
        }

        Assert.Equal(
            HttpStatusCode.NotFound,
            (await ctx.Admin.GetAsync($"/api/ventas/devolvibles?idPuntoVenta={ctx.IdPuntoVenta}&numero=987654321")).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await ctx.Admin.GetAsync($"/api/ventas/{idEspejo}/devolvibles")).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await otro.Admin.GetAsync($"/api/ventas/{venta.Id}/devolvibles")).StatusCode);
        Assert.Equal(
            HttpStatusCode.NotFound,
            (await otro.Admin.GetAsync($"/api/ventas/devolvibles?idPuntoVenta={ctx.IdPuntoVenta}&numero={venta.Numero}")).StatusCode);
    }

    [Fact]
    public async Task LaLecturaDevolviblesExigeUnaSesion()
    {
        using var anonimo = fixture.CreateClient();

        Assert.Equal(HttpStatusCode.Unauthorized, (await anonimo.GetAsync("/api/ventas/devolvibles?idPuntoVenta=1&numero=1")).StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, (await anonimo.GetAsync("/api/ventas/1/devolvibles")).StatusCode);
    }
}
