using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Ways.Application.Abstracciones;
using Ways.Application.Caja;
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

    private static Task<HttpResponseMessage> EnviarDevolucionAsync(
        HttpClient cliente, Contexto ctx, int idCliente, int? idAsociado, decimal reintegro, LineaDeVenta[] lineas) =>
        cliente.PostAsJsonAsync("/api/ventas", new SolicitudDeVenta(
            ctx.IdPuntoVenta, idCliente, "NCX", idAsociado, lineas,
            reintegro > 0m ? [new PagoDeVenta(ctx.IdMedioEfectivo, reintegro, null, 0m)] : [], null, null));

    /// <summary>Emite la devolución reintegrando en efectivo exactamente lo que el servidor dice que vale: manda el pedido sin pagos,
    /// toma <c>datos.reintegro</c> del rechazo <c>devolucion_reintegro_invalido</c> y lo reenvía. Cualquier otro resultado (un rechazo
    /// anterior al del reintegro, o el éxito de una nota de total cero) se devuelve tal cual.</summary>
    private static async Task<HttpResponseMessage> DevolverAsync(
        HttpClient cliente, Contexto ctx, int idCliente, int? idAsociado, params LineaDeVenta[] lineas)
    {
        var sinReintegro = await EnviarDevolucionAsync(cliente, ctx, idCliente, idAsociado, 0m, lineas);
        if (sinReintegro.StatusCode != HttpStatusCode.BadRequest)
        {
            return sinReintegro;
        }

        var problema = JsonDocument.Parse(await sinReintegro.Content.ReadAsStringAsync()).RootElement;
        if (problema.GetProperty("codigo").GetString() != "devolucion_reintegro_invalido")
        {
            return sinReintegro;
        }

        var reintegro = problema.GetProperty("datos").GetProperty("reintegro").GetDecimal();
        return await EnviarDevolucionAsync(cliente, ctx, idCliente, idAsociado, reintegro, lineas);
    }

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
    public async Task AnularUnaDevolucionIntermediaNoHaceQueLoAcreditadoSuperePorUnPesoElTotalDelItem()
    {
        var ctx = await PrepararAsync(nameof(AnularUnaDevolucionIntermediaNoHaceQueLoAcreditadoSuperePorUnPesoElTotalDelItem));
        var idArticulo = await SembrarArticuloAsync(ctx, "dev-intermedia", 33.34m);
        var idCliente = await SembrarClienteAsync(ctx, "Cliente Intermedia");
        var venta = await VenderAsync(ctx, idCliente, 100m, new LineaDeVenta(idArticulo, 3m, null));

        var primera = await DevolverBienAsync(ctx, idCliente, venta.Id, new LineaDeVenta(idArticulo, 1m, null));
        var segunda = await DevolverBienAsync(ctx, idCliente, venta.Id, new LineaDeVenta(idArticulo, 1m, null));
        Assert.Equal([-33m, -34m], [primera.Total, segunda.Total]);

        var anulacion = await ctx.Admin.PostAsync($"/api/ventas/{primera.Id}/anulacion", null);
        Assert.Equal(HttpStatusCode.OK, anulacion.StatusCode);

        var ultima = await DevolverBienAsync(ctx, idCliente, venta.Id, new LineaDeVenta(idArticulo, 2m, null));

        Assert.Equal(-66m, ultima.Total);
        Assert.Equal(-100m, segunda.Total + ultima.Total);
    }

    [Fact]
    public async Task AnularLaDevolucionDelPrimerItemDelMismoArticuloVuelveAAcreditarEseItemYNoElOtro()
    {
        var ctx = await PrepararAsync(nameof(AnularLaDevolucionDelPrimerItemDelMismoArticuloVuelveAAcreditarEseItemYNoElOtro));
        var idArticulo = await SembrarArticuloAsync(ctx, "dev-dos-precios", 100m);
        var idCliente = await SembrarClienteAsync(ctx, "Cliente Dos Precios");

        // El mismo artículo en dos items: el primero con 50 % de descuento manual (50) y el segundo a lista (100).
        var venta = await VenderAsync(
            ctx, idCliente, 150m,
            new LineaDeVenta(idArticulo, 1m, null, AjusteManualPorcentaje: -50m),
            new LineaDeVenta(idArticulo, 1m, null));

        var primera = await DevolverBienAsync(ctx, idCliente, venta.Id, new LineaDeVenta(idArticulo, 1m, null));
        var segunda = await DevolverBienAsync(ctx, idCliente, venta.Id, new LineaDeVenta(idArticulo, 1m, null));
        Assert.Equal([-50m, -100m], [primera.Total, segunda.Total]);

        var anulacion = await ctx.Admin.PostAsync($"/api/ventas/{primera.Id}/anulacion", null);
        Assert.Equal(HttpStatusCode.OK, anulacion.StatusCode);

        var tercera = await DevolverBienAsync(ctx, idCliente, venta.Id, new LineaDeVenta(idArticulo, 1m, null));

        Assert.Equal(-50m, tercera.Total);
        Assert.Equal(-150m, segunda.Total + tercera.Total);

        await using var db = fixture.CrearContextoDeAplicacion(new TenantActualFijo(ModoDeAcceso.Tenant, ctx.IdTenant));
        var itemsDeOrigen = await db.ItemsComprobanteVenta
            .Where(i => i.IdComprobanteVenta == venta.Id)
            .OrderBy(i => i.Orden)
            .Select(i => i.Id)
            .ToListAsync();
        var origenDeLaTercera = await db.ItemsComprobanteVenta
            .Where(i => i.IdComprobanteVenta == tercera.Id)
            .Select(i => i.IdItemComprobanteVentaOrigen)
            .SingleAsync();
        Assert.Equal(itemsDeOrigen[0], origenDeLaTercera);
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

    /// <summary>El espejo de la factura se arma después con TODOS los items del ticket (<c>CrearEspejoAsync</c>): un ticket con la
    /// decisión de facturar todavía debida (marca en NULL) o en <c>pendiente</c> sin espejo no admite devolución, porque lo devuelto
    /// quedaría facturado.</summary>
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task UnTicketConLaDecisionDeFacturarDebidaOPendienteNoSePuedeDevolver(bool conFilaPendiente)
    {
        var ctx = await PrepararAsync($"{nameof(UnTicketConLaDecisionDeFacturarDebidaOPendienteNoSePuedeDevolver)}{conFilaPendiente}");
        var idArticulo = await SembrarArticuloAsync(ctx, "dev-decision", 100m);
        var idCliente = await SembrarClienteAsync(ctx, "Cliente Decision");
        var venta = await VenderAsync(ctx, idCliente, 100m, new LineaDeVenta(idArticulo, 1m, null));

        await using (var db = fixture.CrearContextoDeAplicacion(new TenantActualFijo(ModoDeAcceso.Tenant, ctx.IdTenant)))
        {
            var ahora = DateTimeOffset.UtcNow;
            if (conFilaPendiente)
            {
                db.FacturacionesDeVenta.Add(new FacturacionDeVenta
                {
                    IdTenant = ctx.IdTenant,
                    IdEmpresa = await db.PuntosVenta.Where(p => p.Id == ctx.IdPuntoVenta).Select(p => p.IdEmpresa).SingleAsync(),
                    IdComprobanteVenta = venta.Id, Motivo = MotivoDeFacturacion.Forzada, Aportes = "[]",
                    Periodo = new DateOnly(2026, 11, 1), CreatedAt = ahora, UpdatedAt = ahora
                });
            }
            else
            {
                (await db.ComprobantesVenta.SingleAsync(c => c.Id == venta.Id)).FacturacionDecididaEn = null;
            }

            await db.SaveChangesAsync();
        }

        await AfirmarErrorAsync(
            await DevolverAsync(ctx.Admin, ctx, idCliente, venta.Id, new LineaDeVenta(idArticulo, 1m, null)),
            HttpStatusCode.Conflict, "devolucion_requiere_nota_de_credito_fiscal");
        Assert.Equal(0, await ContarNcxAsync(ctx, venta.Id));

        var lectura = await LeerDevolviblesAsync(await ctx.Admin.GetAsync($"/api/ventas/{venta.Id}/devolvibles"));
        Assert.False(lectura.Devolvible);
        Assert.Equal("con_factura_fiscal", lectura.Motivo);
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
    // ---- Reintegro en efectivo: sale del arqueo del turno -------------------------------------------

    private async Task<int> IdDelTurnoAbiertoAsync(Contexto ctx)
    {
        await using var db = fixture.CrearContextoDeAplicacion(new TenantActualFijo(ModoDeAcceso.Tenant, ctx.IdTenant));
        return await db.TurnosCaja
            .Where(t => t.IdPuntoVenta == ctx.IdPuntoVenta && t.Estado == Ways.Domain.Caja.EstadoTurno.Abierto)
            .Select(t => t.Id).SingleAsync();
    }

    private static async Task<decimal> EsperadoEnEfectivoAsync(Contexto ctx, int idTurno)
    {
        var resumen = await ctx.Admin.GetFromJsonAsync<ResumenDeTurno>($"/api/caja/turnos/{idTurno}/resumen", OpcionesJson);
        return resumen!.Medios.Single(m => m.IdMedioPago == ctx.IdMedioEfectivo).ImporteEsperado;
    }

    /// <summary>La cláusula bajo prueba es el <c>importe * signo</c> de <c>LectorDeMovimientosDelTurno</c>: el cobro de la NCX se guarda
    /// positivo y, sin el signo, el esperado SUBIRÍA con la devolución (200 -> 300) en vez de bajar.</summary>
    [Fact]
    public async Task ElReintegroEnEfectivoBajaElEsperadoDelTurnoYElCierreCuadra()
    {
        var ctx = await PrepararAsync(nameof(ElReintegroEnEfectivoBajaElEsperadoDelTurnoYElCierreCuadra));
        var idArticulo = await SembrarArticuloAsync(ctx, "dev-efectivo", 100m);
        var idCliente = await SembrarClienteAsync(ctx, "Cliente Efectivo");
        var idTurno = await IdDelTurnoAbiertoAsync(ctx);
        var venta = await VenderAsync(ctx, idCliente, 200m, new LineaDeVenta(idArticulo, 2m, null));
        Assert.Equal(200m, await EsperadoEnEfectivoAsync(ctx, idTurno));

        var devolucion = await DevolverBienAsync(ctx, idCliente, venta.Id, new LineaDeVenta(idArticulo, 1m, null));

        Assert.Equal(-100m, devolucion.Total);
        var reintegro = Assert.Single(devolucion.Pagos);
        Assert.Equal(ctx.IdMedioEfectivo, reintegro.IdMedioPago);
        Assert.Equal(100m, reintegro.Importe);
        Assert.Equal(100m, await EsperadoEnEfectivoAsync(ctx, idTurno));

        var cierre = await ctx.Admin.PostAsJsonAsync(
            $"/api/caja/turnos/{idTurno}/cierre", new SolicitudDeCierre([new ConteoDeclarado(ctx.IdMedioEfectivo, 100m)], null));
        var cuerpo = await cierre.Content.ReadAsStringAsync();
        Assert.True(cierre.StatusCode == HttpStatusCode.OK, cuerpo);
        var arqueo = Assert.Single(JsonSerializer.Deserialize<TurnoConArqueos>(cuerpo, OpcionesJson)!.Arqueos);
        Assert.Equal(100m, arqueo.ImporteEsperado);
        Assert.Equal(0m, arqueo.Diferencia);
    }

    /// <summary>La cláusula bajo prueba es el signo del importe por medio en <c>LeerVentasDelTurnoAsync</c>: el cobro de la NCX se
    /// guarda positivo y, sin el signo, "Ventas del turno" mostraría efectivo 300 (200 + 100) contra un esperado de 100.</summary>
    [Fact]
    public async Task ElTotalPorMedioDeVentasDelTurnoDescuentaLaDevolucionYCoincideConElEsperado()
    {
        var ctx = await PrepararAsync(nameof(ElTotalPorMedioDeVentasDelTurnoDescuentaLaDevolucionYCoincideConElEsperado));
        var idArticulo = await SembrarArticuloAsync(ctx, "dev-listado", 100m);
        var idCliente = await SembrarClienteAsync(ctx, "Cliente Listado");
        var idTurno = await IdDelTurnoAbiertoAsync(ctx);
        var venta = await VenderAsync(ctx, idCliente, 200m, new LineaDeVenta(idArticulo, 2m, null));
        var devolucion = await DevolverBienAsync(ctx, idCliente, venta.Id, new LineaDeVenta(idArticulo, 1m, null));

        var ventas = (await ctx.Admin.GetFromJsonAsync<List<VentaDeTurnoListado>>($"/api/ventas/por-turno/{idTurno}", OpcionesJson))!;

        var filaDeLaDevolucion = ventas.Single(v => v.Id == devolucion.Id);
        Assert.Equal(-100m, filaDeLaDevolucion.Total);
        Assert.Equal(-100m, Assert.Single(filaDeLaDevolucion.MediosDePago).Importe);

        var efectivoListado = ventas
            .Where(v => v.Estado == EstadoComprobante.Emitido)
            .SelectMany(v => v.MediosDePago)
            .Where(m => m.IdMedioPago == ctx.IdMedioEfectivo)
            .Sum(m => m.Importe);
        Assert.Equal(100m, efectivoListado);
        Assert.Equal(await EsperadoEnEfectivoAsync(ctx, idTurno), efectivoListado);
    }

    [Fact]
    public async Task AnularLaDevolucionDevuelveElEfectivoAlEsperadoDelTurno()
    {
        var ctx = await PrepararAsync(nameof(AnularLaDevolucionDevuelveElEfectivoAlEsperadoDelTurno));
        var idArticulo = await SembrarArticuloAsync(ctx, "dev-anula-efectivo", 100m);
        var idCliente = await SembrarClienteAsync(ctx, "Cliente Anula Efectivo");
        var idTurno = await IdDelTurnoAbiertoAsync(ctx);
        var venta = await VenderAsync(ctx, idCliente, 300m, new LineaDeVenta(idArticulo, 3m, null));
        var primera = await DevolverBienAsync(ctx, idCliente, venta.Id, new LineaDeVenta(idArticulo, 1m, null));
        await DevolverBienAsync(ctx, idCliente, venta.Id, new LineaDeVenta(idArticulo, 1m, null));
        Assert.Equal(100m, await EsperadoEnEfectivoAsync(ctx, idTurno));

        var anulacion = await ctx.Admin.PostAsync($"/api/ventas/{primera.Id}/anulacion", null);
        Assert.Equal(HttpStatusCode.OK, anulacion.StatusCode);

        Assert.Equal(200m, await EsperadoEnEfectivoAsync(ctx, idTurno));
    }

    [Fact]
    public async Task UnaDevolucionSinReintegroOConUnReintegroDistintoDelTotalSeRechazaYNoPersisteNada()
    {
        var ctx = await PrepararAsync(nameof(UnaDevolucionSinReintegroOConUnReintegroDistintoDelTotalSeRechazaYNoPersisteNada));
        var idArticulo = await SembrarArticuloAsync(ctx, "dev-reintegro-invalido", 100m);
        var idCliente = await SembrarClienteAsync(ctx, "Cliente Reintegro Invalido");
        var venta = await VenderAsync(ctx, idCliente, 200m, new LineaDeVenta(idArticulo, 2m, null));
        var lineas = new[] { new LineaDeVenta(idArticulo, 1m, null) };

        var sinPagos = await EnviarDevolucionAsync(ctx.Admin, ctx, idCliente, venta.Id, 0m, lineas);
        await AfirmarErrorAsync(sinPagos, HttpStatusCode.BadRequest, "devolucion_reintegro_invalido");
        var datos = JsonDocument.Parse(await sinPagos.Content.ReadAsStringAsync()).RootElement.GetProperty("datos");
        Assert.Equal(100m, datos.GetProperty("reintegro").GetDecimal());

        await AfirmarErrorAsync(
            await EnviarDevolucionAsync(ctx.Admin, ctx, idCliente, venta.Id, 99m, lineas),
            HttpStatusCode.BadRequest, "devolucion_reintegro_invalido");
        await AfirmarErrorAsync(
            await EnviarDevolucionAsync(ctx.Admin, ctx, idCliente, venta.Id, 101m, lineas),
            HttpStatusCode.BadRequest, "devolucion_reintegro_invalido");

        Assert.Equal(0, await ContarNcxAsync(ctx, venta.Id));
        Assert.Equal(-2m, await StockAsync(ctx, idArticulo));
    }

    [Fact]
    public async Task ElReintegroSoloAdmiteEfectivoSinVuelto()
    {
        var ctx = await PrepararAsync(nameof(ElReintegroSoloAdmiteEfectivoSinVuelto));
        var idArticulo = await SembrarArticuloAsync(ctx, "dev-solo-efectivo", 100m);
        var idCliente = await SembrarClienteAsync(ctx, "Cliente Solo Efectivo");
        var venta = await VenderAsync(ctx, idCliente, 100m, new LineaDeVenta(idArticulo, 1m, null));
        int idElectronico;
        await using (var db = fixture.CrearContextoDeAplicacion(new TenantActualFijo(ModoDeAcceso.Tenant, ctx.IdTenant)))
        {
            idElectronico = await db.MediosPago
                .Where(m => m.Comportamiento == ComportamientoMedioPago.Electronico).Select(m => m.Id).FirstAsync();
        }

        var lineas = new[] { new LineaDeVenta(idArticulo, 1m, null) };
        var conTarjeta = await ctx.Admin.PostAsJsonAsync("/api/ventas", new SolicitudDeVenta(
            ctx.IdPuntoVenta, idCliente, "NCX", venta.Id, lineas, [new PagoDeVenta(idElectronico, 100m, "ref", 0m)], null, null));
        await AfirmarErrorAsync(conTarjeta, HttpStatusCode.BadRequest, "devolucion_solo_en_efectivo");

        var conVuelto = await ctx.Admin.PostAsJsonAsync("/api/ventas", new SolicitudDeVenta(
            ctx.IdPuntoVenta, idCliente, "NCX", venta.Id, lineas, [new PagoDeVenta(ctx.IdMedioEfectivo, 150m, null, 50m)], null, null));
        await AfirmarErrorAsync(conVuelto, HttpStatusCode.BadRequest, "devolucion_con_vuelto");

        Assert.Equal(0, await ContarNcxAsync(ctx, venta.Id));
    }
    // ---- Vista previa del reintegro -----------------------------------------------------------------

    private static Task<HttpResponseMessage> PrevisualizarAsync(
        HttpClient cliente, Contexto ctx, int idAsociado, params LineaDeVenta[] lineas) =>
        cliente.PostAsJsonAsync(
            "/api/ventas/devoluciones/previsualizacion", new SolicitudDePrevisualizacionDeDevolucion(ctx.IdPuntoVenta, idAsociado, lineas));

    /// <summary>La vista previa usa el mismo reparto que la emisión: con 3 unidades por 100 el crédito es 33, 34 y 33, y lo que previsualiza es
    /// exactamente el total y el efectivo de la nota emitida a continuación.</summary>
    [Fact]
    public async Task LaVistaPreviaCoincideConElTotalYElReintegroDeLaNotaEmitida()
    {
        var ctx = await PrepararAsync(nameof(LaVistaPreviaCoincideConElTotalYElReintegroDeLaNotaEmitida));
        var idArticulo = await SembrarArticuloAsync(ctx, "dev-previa", 33.34m);
        var idCliente = await SembrarClienteAsync(ctx, "Cliente Previa");
        var venta = await VenderAsync(ctx, idCliente, 100m, new LineaDeVenta(idArticulo, 3m, null));

        foreach (var esperado in new[] { 33m, 34m, 33m })
        {
            var respuesta = await PrevisualizarAsync(ctx.Admin, ctx, venta.Id, new LineaDeVenta(idArticulo, 1m, null));
            var cuerpo = await respuesta.Content.ReadAsStringAsync();
            Assert.True(respuesta.StatusCode == HttpStatusCode.OK, cuerpo);
            var previa = JsonSerializer.Deserialize<PrevisualizacionDeDevolucion>(cuerpo, OpcionesJson)!;
            Assert.Equal(esperado, previa.Reintegro);
            Assert.Equal(esperado, Assert.Single(previa.Lineas).Credito);

            var pedido = new SolicitudDeVenta(
                ctx.IdPuntoVenta, idCliente, "NCX", venta.Id, [new LineaDeVenta(idArticulo, 1m, null)],
                [new PagoDeVenta(ctx.IdMedioEfectivo, previa.Reintegro, null, 0m)], null, null);
            var emitida = await ctx.Admin.PostAsJsonAsync("/api/ventas", pedido);
            var emitidaCuerpo = await emitida.Content.ReadAsStringAsync();
            Assert.True(emitida.StatusCode == HttpStatusCode.Created, emitidaCuerpo);
            Assert.Equal(-previa.Reintegro, JsonSerializer.Deserialize<ComprobanteEmitido>(emitidaCuerpo, OpcionesJson)!.Total);
        }
    }

    [Fact]
    public async Task LaVistaPreviaNoEscribeNadaYRechazaConLosMismosCodigosQueLaEmision()
    {
        var ctx = await PrepararAsync(nameof(LaVistaPreviaNoEscribeNadaYRechazaConLosMismosCodigosQueLaEmision));
        var idArticulo = await SembrarArticuloAsync(ctx, "dev-previa-tope", 100m);
        var idCliente = await SembrarClienteAsync(ctx, "Cliente Previa Tope");
        var venta = await VenderAsync(ctx, idCliente, 100m, new LineaDeVenta(idArticulo, 1m, null));

        await AfirmarErrorAsync(
            await PrevisualizarAsync(ctx.Admin, ctx, venta.Id, new LineaDeVenta(idArticulo, 2m, null)),
            HttpStatusCode.Conflict, "devolucion_excede_lo_vendido");
        await AfirmarErrorAsync(
            await ctx.Admin.PostAsJsonAsync(
                "/api/ventas/devoluciones/previsualizacion",
                new SolicitudDePrevisualizacionDeDevolucion(ctx.IdPuntoVenta, venta.Id, [])),
            HttpStatusCode.BadRequest, "lineas_requeridas");

        Assert.Equal(0, await ContarNcxAsync(ctx, venta.Id));
        Assert.Equal(-1m, await StockAsync(ctx, idArticulo));
    }

    [Fact]
    public async Task LaVistaPreviaDeUnTicketConFacturaFiscalEsElMismo409QueLaEmision()
    {
        var ctx = await PrepararAsync(nameof(LaVistaPreviaDeUnTicketConFacturaFiscalEsElMismo409QueLaEmision));
        var idArticulo = await SembrarArticuloAsync(ctx, "dev-previa-cae", 100m);
        var idCliente = await SembrarClienteAsync(ctx, "Cliente Previa Cae");
        var venta = await VenderAsync(ctx, idCliente, 100m, new LineaDeVenta(idArticulo, 1m, null));
        await SembrarEspejoAsync(ctx, venta.Id, idCliente, ResultadoFiscal.Aprobado);

        await AfirmarErrorAsync(
            await PrevisualizarAsync(ctx.Admin, ctx, venta.Id, new LineaDeVenta(idArticulo, 1m, null)),
            HttpStatusCode.Conflict, "devolucion_requiere_nota_de_credito_fiscal");
    }

    [Fact]
    public async Task LaVistaPreviaDeUnTicketDeOtroTenantEsNotFoundYExigeSesion()
    {
        var ctx = await PrepararAsync(nameof(LaVistaPreviaDeUnTicketDeOtroTenantEsNotFoundYExigeSesion));
        var otro = await PrepararAsync(nameof(LaVistaPreviaDeUnTicketDeOtroTenantEsNotFoundYExigeSesion) + "Otro");
        var idArticulo = await SembrarArticuloAsync(ctx, "dev-previa-404", 100m);
        var idCliente = await SembrarClienteAsync(ctx, "Cliente Previa 404");
        var venta = await VenderAsync(ctx, idCliente, 100m, new LineaDeVenta(idArticulo, 1m, null));

        Assert.Equal(
            HttpStatusCode.NotFound,
            (await otro.Admin.PostAsJsonAsync(
                "/api/ventas/devoluciones/previsualizacion",
                new SolicitudDePrevisualizacionDeDevolucion(ctx.IdPuntoVenta, venta.Id, [new LineaDeVenta(idArticulo, 1m, null)]))).StatusCode);
        Assert.Equal(
            HttpStatusCode.NotFound,
            (await ctx.Admin.PostAsJsonAsync(
                "/api/ventas/devoluciones/previsualizacion",
                new SolicitudDePrevisualizacionDeDevolucion(ctx.IdPuntoVenta, 987654321, [new LineaDeVenta(idArticulo, 1m, null)]))).StatusCode);

        using var anonimo = fixture.CreateClient();
        Assert.Equal(
            HttpStatusCode.Unauthorized,
            (await anonimo.PostAsJsonAsync(
                "/api/ventas/devoluciones/previsualizacion",
                new SolicitudDePrevisualizacionDeDevolucion(1, 1, [new LineaDeVenta(1, 1m, null)]))).StatusCode);
    }
}
