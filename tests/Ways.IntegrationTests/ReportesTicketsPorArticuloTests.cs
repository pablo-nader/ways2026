using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.EntityFrameworkCore;
using Ways.Application.Abstracciones;
using Ways.Application.Organizacion;
using Ways.Application.Parametros;
using Ways.Application.Reportes;
using Ways.Application.Usuarios;
using Ways.Domain.Articulos;
using Ways.Domain.Catalogos;
using Ways.Domain.Clientes;
using Ways.Domain.Fiscal;
using Ways.Domain.Organizacion;
using Ways.Domain.Usuarios;
using Ways.Domain.Ventas;
using Ways.Infrastructure.Multitenancy;

namespace Ways.IntegrationTests;

/// <summary>
/// <c>GET /api/reportes/ventas/por-articulo/{idArticulo}/tickets</c> punta a punta: un renglón por
/// comprobante con líneas del artículo, con las mismas reglas de inclusión que el reporte por
/// artículo. Siembra directa con fechas fijas a mediodía UTC.
/// </summary>
[Collection("Ways.IntegrationTests secuencial")]
public class ReportesTicketsPorArticuloTests(WaysApiFixture fixture) : IClassFixture<WaysApiFixture>
{
    private const string PasswordRoot = "root";
    private const string MailRoot = "test@test.com";
    private const string PasswordOtroRol = "otro-rol-password-larga";

    private static readonly JsonSerializerOptions OpcionesJson = new() { PropertyNameCaseInsensitive = true };
    private static readonly DateOnly Dia = new(2026, 8, 5);
    private static readonly DateTimeOffset Mediodia = new(2026, 8, 5, 12, 0, 0, TimeSpan.Zero);

    private static long _numeroSecuencial = 1000;

    private sealed record Contexto(
        int IdTenant, int IdEmpresa, int IdPuntoVenta, HttpClient Admin, HttpClient Supervisor, HttpClient Vendedor,
        int IdCliente, int IdEmpleadoAdmin, int IdTipoTx, int IdTipoNcx, int IdListaPrecio, int IdAlicuotaIva, int IdArea);

    private async Task<Contexto> PrepararAsync(string nombre, WebApplicationFactory<Program>? host = null)
    {
        host ??= fixture;
        var root = host.CreateClient();
        var loginRoot = await root.PostAsJsonAsync("/api/auth/login", new SolicitudDeLogin(MailRoot, PasswordRoot));
        Assert.Equal(HttpStatusCode.OK, loginRoot.StatusCode);

        var mailAdmin = $"{nombre.ToLowerInvariant()}@ways.test";
        var solicitud = new SolicitudDeAprovisionamiento(nombre, $"{nombre} SA", "Local 1", mailAdmin, ModoPuntoVenta.Web);
        var respuesta = await root.PostAsJsonAsync("/api/plataforma/tenants", solicitud);
        Assert.Equal(HttpStatusCode.Created, respuesta.StatusCode);
        var resultado = (await respuesta.Content.ReadFromJsonAsync<ResultadoAprovisionamiento>())!;

        var admin = host.CreateClient();
        var loginAdmin = await admin.PostAsJsonAsync(
            "/api/auth/login", new SolicitudDeLogin(mailAdmin, resultado.PasswordTemporal));
        Assert.Equal(HttpStatusCode.OK, loginAdmin.StatusCode);

        var supervisor = await CrearYLoguearAsync(admin, host, nombre, "supervisor", RolConocido.Supervisor);
        var vendedor = await CrearYLoguearAsync(admin, host, nombre, "vendedor", RolConocido.Vendedor);

        await using var db = fixture.CrearContextoDeAplicacion(new TenantActualFijo(ModoDeAcceso.Tenant, resultado.IdTenant));
        var ahora = DateTimeOffset.UtcNow;
        var modelo = await db.Clientes.FirstAsync();
        var cliente = new Cliente
        {
            IdTenant = resultado.IdTenant, Numero = 2, Nombre = "Cliente de prueba",
            IdCondicionFiscal = modelo.IdCondicionFiscal, IdListaPrecio = modelo.IdListaPrecio,
            CreatedAt = ahora, UpdatedAt = ahora
        };
        db.Clientes.Add(cliente);
        var area = new Area { IdTenant = resultado.IdTenant, Nombre = "Area", Orden = 1, CreatedAt = ahora, UpdatedAt = ahora };
        db.Areas.Add(area);
        await db.SaveChangesAsync();

        var idListaPrecio = await db.ListasPrecio.Select(l => l.Id).FirstAsync();
        var idAlicuotaIva = await db.AlicuotasIva.Select(a => a.Id).FirstAsync();

        await using var dbPlataforma = fixture.CrearContextoDeAplicacion(TenantActualFijo.Plataforma);
        var idTx = await dbPlataforma.TiposComprobante.Where(t => t.Codigo == "TX").Select(t => t.Id).SingleAsync();
        var idNcx = await dbPlataforma.TiposComprobante.Where(t => t.Codigo == "NCX").Select(t => t.Id).SingleAsync();

        return new Contexto(
            resultado.IdTenant, resultado.IdEmpresa, resultado.IdPuntoVenta, admin, supervisor, vendedor,
            cliente.Id, resultado.IdUsuarioAdmin, idTx, idNcx, idListaPrecio, idAlicuotaIva, area.Id);
    }

    private static async Task<HttpClient> CrearYLoguearAsync(
        HttpClient admin, WebApplicationFactory<Program> host, string nombre, string sufijo, RolConocido rol)
    {
        var corto = Guid.NewGuid().ToString("N")[..8];
        var mail = $"{nombre.ToLowerInvariant()}-{sufijo}@ways.test";
        var alta = await admin.PostAsJsonAsync("/api/usuarios", new CrearUsuario($"{sufijo}-{corto}", mail, (int)rol, PasswordOtroRol));
        Assert.Equal(HttpStatusCode.Created, alta.StatusCode);

        var cliente = host.CreateClient();
        var login = await cliente.PostAsJsonAsync("/api/auth/login", new SolicitudDeLogin(mail, PasswordOtroRol));
        Assert.Equal(HttpStatusCode.OK, login.StatusCode);
        return cliente;
    }

    private async Task<int> SembrarArticuloAsync(Contexto ctx, string nombre)
    {
        await using var db = fixture.CrearContextoDeAplicacion(new TenantActualFijo(ModoDeAcceso.Tenant, ctx.IdTenant));
        var ahora = DateTimeOffset.UtcNow;
        var articulo = new Articulo
        {
            IdTenant = ctx.IdTenant, CodigoInterno = $"{nombre}-{Guid.NewGuid():N}", Nombre = nombre,
            IdArea = ctx.IdArea, IdAlicuotaIva = ctx.IdAlicuotaIva, UnidadVenta = UnidadVenta.Unidad,
            EsProducto = true, CreatedAt = ahora, UpdatedAt = ahora
        };
        db.Articulos.Add(articulo);
        await db.SaveChangesAsync();
        return articulo.Id;
    }

    private async Task<int> SembrarPuntoVentaAsync(Contexto ctx, string nombre)
    {
        await using var db = fixture.CrearContextoDeAplicacion(new TenantActualFijo(ModoDeAcceso.Tenant, ctx.IdTenant));
        var ahora = DateTimeOffset.UtcNow;
        var puntoVenta = new PuntoVenta
        {
            IdTenant = ctx.IdTenant, IdEmpresa = ctx.IdEmpresa, Nombre = nombre, Modo = ModoPuntoVenta.Web,
            CreatedAt = ahora, UpdatedAt = ahora
        };
        db.PuntosVenta.Add(puntoVenta);
        await db.SaveChangesAsync();
        return puntoVenta.Id;
    }

    /// <summary>Un comprobante con las líneas dadas (artículo, cantidad, total); los importes
    /// llevan el signo (negativos en una NCX). Devuelve el id del comprobante.</summary>
    private async Task<int> SembrarTicketAsync(
        Contexto ctx, IReadOnlyList<(int IdArticulo, decimal Cantidad, decimal Total)> lineas,
        DateTimeOffset? fecha = null, bool esNcx = false, EstadoComprobante estado = EstadoComprobante.Emitido,
        int? idPuntoVenta = null, bool consumidorFinal = false, long? numero = null,
        int? idVentaOrigenDeEspejo = null)
    {
        await using var db = fixture.CrearContextoDeAplicacion(new TenantActualFijo(ModoDeAcceso.Tenant, ctx.IdTenant));
        var ahora = DateTimeOffset.UtcNow;
        var total = lineas.Sum(l => l.Total);
        var espejo = idVentaOrigenDeEspejo is not null;

        var idCliente = consumidorFinal
            ? await db.Clientes.Where(c => c.Numero == 1).Select(c => c.Id).SingleAsync()
            : ctx.IdCliente;
        var idTipo = espejo
            ? await fixture.CrearContextoDeAplicacion(TenantActualFijo.Plataforma)
                .TiposComprobante.Where(t => t.Codigo == "FA").Select(t => t.Id).SingleAsync()
            : esNcx ? ctx.IdTipoNcx : ctx.IdTipoTx;

        var comprobante = new ComprobanteVenta
        {
            IdTenant = ctx.IdTenant,
            IdTipoComprobante = idTipo,
            Numero = numero ?? Interlocked.Increment(ref _numeroSecuencial),
            Fecha = fecha ?? Mediodia,
            IdPuntoVenta = idPuntoVenta ?? ctx.IdPuntoVenta,
            IdEmpleado = ctx.IdEmpleadoAdmin,
            IdCliente = idCliente,
            Subtotal = total,
            DescuentoTotal = 0m,
            Total = total,
            Estado = estado,
            Cae = espejo ? "12345678901234" : null,
            CaeVencimiento = espejo ? new DateOnly(2026, 9, 20) : null,
            ResultadoFiscal = espejo ? ResultadoFiscal.Aprobado : null,
            IdVentaOrigen = idVentaOrigenDeEspejo,
            CreatedAt = ahora,
            UpdatedAt = ahora
        };
        db.ComprobantesVenta.Add(comprobante);
        await db.SaveChangesAsync();

        var orden = 0;
        foreach (var (idArticulo, cantidad, totalLinea) in lineas)
        {
            db.ItemsComprobanteVenta.Add(new ItemComprobanteVenta
            {
                IdTenant = ctx.IdTenant, IdComprobanteVenta = comprobante.Id, Orden = ++orden, IdArticulo = idArticulo,
                Descripcion = "snapshot", IdArea = ctx.IdArea, IdListaPrecio = ctx.IdListaPrecio,
                IdAlicuotaIva = ctx.IdAlicuotaIva, PorcentajeIva = 0m, Cantidad = cantidad,
                PrecioUnitario = totalLinea / cantidad, Descuento = 0m, Total = totalLinea,
                CreatedAt = ahora, UpdatedAt = ahora
            });
        }

        await db.SaveChangesAsync();
        return comprobante.Id;
    }

    private static string Url(
        int idEmpresa, int idArticulo, DateOnly? desde = null, DateOnly? hasta = null, int? idPuntoVenta = null) =>
        $"/api/reportes/ventas/por-articulo/{idArticulo}/tickets?idEmpresa={idEmpresa}" +
        $"&desde={desde ?? Dia:yyyy-MM-dd}&hasta={hasta ?? Dia:yyyy-MM-dd}" +
        (idPuntoVenta is { } pv ? $"&idPuntoVenta={pv}" : string.Empty);

    private static string UrlReporte(int idEmpresa, int idArticulo, int? idPuntoVenta = null) =>
        $"/api/reportes/ventas/por-articulo?idEmpresa={idEmpresa}&desde={Dia:yyyy-MM-dd}&hasta={Dia:yyyy-MM-dd}" +
        $"&idArticulo={idArticulo}" + (idPuntoVenta is { } pv ? $"&idPuntoVenta={pv}" : string.Empty);

    private static async Task<T> LeerAsync<T>(HttpClient cliente, string url)
    {
        var respuesta = await cliente.GetAsync(url);
        var cuerpo = await respuesta.Content.ReadAsStringAsync();
        Assert.True(respuesta.StatusCode == HttpStatusCode.OK, cuerpo);
        return JsonSerializer.Deserialize<T>(cuerpo, OpcionesJson)!;
    }

    [Fact]
    public async Task UnRenglonPorComprobanteSumaLasLineasDelArticuloYOmiteLasDeOtros()
    {
        var ctx = await PrepararAsync(nameof(UnRenglonPorComprobanteSumaLasLineasDelArticuloYOmiteLasDeOtros));
        var cola = await SembrarArticuloAsync(ctx, "Cola");
        var agua = await SembrarArticuloAsync(ctx, "Agua");
        var ticket = await SembrarTicketAsync(ctx, [(cola, 2m, 200m), (agua, 5m, 999m), (cola, 1m, 120m)]);

        var resultado = await LeerAsync<TicketsDeArticulo>(ctx.Admin, Url(ctx.IdEmpresa, cola));

        var fila = Assert.Single(resultado.Tickets);
        Assert.Equal(ticket, fila.IdComprobante);
        Assert.Equal(3m, fila.Cantidad);
        Assert.Equal(320m, fila.Total);
        Assert.Equal(3m, resultado.CantidadTotal);
        Assert.Equal(320m, resultado.Total);
        Assert.Equal(cola, resultado.IdArticulo);
        Assert.Equal("Cola", resultado.Descripcion);
        Assert.False(string.IsNullOrWhiteSpace(fila.PuntoVenta));
        Assert.False(string.IsNullOrWhiteSpace(resultado.ZonaHoraria));
    }

    [Fact]
    public async Task LaNotaDeCreditoApareceNegativaYRestaDelTotal()
    {
        var ctx = await PrepararAsync(nameof(LaNotaDeCreditoApareceNegativaYRestaDelTotal));
        var cola = await SembrarArticuloAsync(ctx, "Cola");
        var venta = await SembrarTicketAsync(ctx, [(cola, 10m, 1000m)]);
        var nota = await SembrarTicketAsync(ctx, [(cola, -2m, -200m)], fecha: Mediodia.AddHours(1), esNcx: true);

        var resultado = await LeerAsync<TicketsDeArticulo>(ctx.Admin, Url(ctx.IdEmpresa, cola));

        Assert.Equal([nota, venta], resultado.Tickets.Select(t => t.IdComprobante));
        Assert.Equal(-2m, resultado.Tickets[0].Cantidad);
        Assert.Equal(-200m, resultado.Tickets[0].Total);
        Assert.Equal(8m, resultado.CantidadTotal);
        Assert.Equal(800m, resultado.Total);
    }

    [Fact]
    public async Task UnComprobanteAnuladoNoApareceNiSuma()
    {
        var ctx = await PrepararAsync(nameof(UnComprobanteAnuladoNoApareceNiSuma));
        var cola = await SembrarArticuloAsync(ctx, "Cola");
        await SembrarTicketAsync(ctx, [(cola, 10m, 999_999m)], estado: EstadoComprobante.Anulado);
        var valido = await SembrarTicketAsync(ctx, [(cola, 3m, 250m)]);

        var resultado = await LeerAsync<TicketsDeArticulo>(ctx.Admin, Url(ctx.IdEmpresa, cola));

        Assert.Equal(valido, Assert.Single(resultado.Tickets).IdComprobante);
        Assert.Equal(250m, resultado.Total);
    }

    [Fact]
    public async Task LaVentaConEspejoFiscalApareceUnaSolaVez()
    {
        var ctx = await PrepararAsync(nameof(LaVentaConEspejoFiscalApareceUnaSolaVez));
        var cola = await SembrarArticuloAsync(ctx, "Cola");
        var venta = await SembrarTicketAsync(ctx, [(cola, 2m, 200m)]);
        await SembrarTicketAsync(ctx, [(cola, 2m, 200m)], idVentaOrigenDeEspejo: venta);

        var resultado = await LeerAsync<TicketsDeArticulo>(ctx.Admin, Url(ctx.IdEmpresa, cola));

        Assert.Equal(venta, Assert.Single(resultado.Tickets).IdComprobante);
        Assert.Equal(200m, resultado.Total);
    }

    [Fact]
    public async Task LosTicketsVanDelMasRecienteAlMasAntiguoYLosTotalesCoincidenConElReporte()
    {
        var ctx = await PrepararAsync(nameof(LosTicketsVanDelMasRecienteAlMasAntiguoYLosTotalesCoincidenConElReporte));
        var cola = await SembrarArticuloAsync(ctx, "Cola");
        var primero = await SembrarTicketAsync(ctx, [(cola, 1m, 100m)], fecha: Mediodia.AddHours(-3));
        var empateA = await SembrarTicketAsync(ctx, [(cola, 2m, 220m)], fecha: Mediodia);
        var empateB = await SembrarTicketAsync(ctx, [(cola, 3m, 330m)], fecha: Mediodia);
        var nota = await SembrarTicketAsync(ctx, [(cola, -1m, -110m)], fecha: Mediodia.AddHours(2), esNcx: true);

        var resultado = await LeerAsync<TicketsDeArticulo>(ctx.Admin, Url(ctx.IdEmpresa, cola));
        var reporte = await LeerAsync<VentasPorArticulo>(ctx.Admin, UrlReporte(ctx.IdEmpresa, cola));

        Assert.Equal([nota, empateB, empateA, primero], resultado.Tickets.Select(t => t.IdComprobante));
        var fila = Assert.Single(reporte.Articulos);
        Assert.Equal(fila.Cantidad, resultado.CantidadTotal);
        Assert.Equal(fila.Total, resultado.Total);
        Assert.Equal(resultado.Tickets.Sum(t => t.Total), resultado.Total);
    }

    [Fact]
    public async Task ElFiltroDePuntoDeVentaLimitaLosTickets()
    {
        var ctx = await PrepararAsync(nameof(ElFiltroDePuntoDeVentaLimitaLosTickets));
        var cola = await SembrarArticuloAsync(ctx, "Cola");
        var otroPv = await SembrarPuntoVentaAsync(ctx, "Sucursal Norte");
        var enPrimero = await SembrarTicketAsync(ctx, [(cola, 1m, 100m)]);
        var enOtro = await SembrarTicketAsync(ctx, [(cola, 4m, 400m)], idPuntoVenta: otroPv);

        var todos = await LeerAsync<TicketsDeArticulo>(ctx.Admin, Url(ctx.IdEmpresa, cola));
        var soloOtro = await LeerAsync<TicketsDeArticulo>(ctx.Admin, Url(ctx.IdEmpresa, cola, idPuntoVenta: otroPv));
        var reporteOtro = await LeerAsync<VentasPorArticulo>(ctx.Admin, UrlReporte(ctx.IdEmpresa, cola, otroPv));

        Assert.Equal(new[] { enPrimero, enOtro }.Order(), todos.Tickets.Select(t => t.IdComprobante).Order());
        var fila = Assert.Single(soloOtro.Tickets);
        Assert.Equal(enOtro, fila.IdComprobante);
        Assert.Equal("Sucursal Norte", fila.PuntoVenta);
        Assert.Equal(400m, soloOtro.Total);
        Assert.Equal(Assert.Single(reporteOtro.Articulos).Total, soloOtro.Total);
    }

    [Fact]
    public async Task ElRangoDeFechasSeCortaPorLaZonaDelPuntoDeVenta()
    {
        var ctx = await PrepararAsync(nameof(ElRangoDeFechasSeCortaPorLaZonaDelPuntoDeVenta));
        var cola = await SembrarArticuloAsync(ctx, "Noche");
        // 2026-08-05T22:30-03:00 == 2026-08-06T01:30Z
        await SembrarTicketAsync(ctx, [(cola, 1m, 500m)], fecha: new DateTimeOffset(2026, 8, 6, 1, 30, 0, TimeSpan.Zero));

        var arg = await ctx.Admin.PutAsJsonAsync(
            "/api/parametros?idEmpresa=" + ctx.IdEmpresa,
            new ParametroAlta("zona_horaria", "\"America/Argentina/Buenos_Aires\"", null));
        Assert.Equal(HttpStatusCode.OK, arg.StatusCode);
        var enArt = await LeerAsync<TicketsDeArticulo>(ctx.Admin, Url(ctx.IdEmpresa, cola));
        var enArtSiguiente = await LeerAsync<TicketsDeArticulo>(
            ctx.Admin, Url(ctx.IdEmpresa, cola, Dia.AddDays(1), Dia.AddDays(1)));

        var utc = await ctx.Admin.PutAsJsonAsync(
            "/api/parametros?idEmpresa=" + ctx.IdEmpresa, new ParametroAlta("zona_horaria", "\"UTC\"", null));
        Assert.Equal(HttpStatusCode.OK, utc.StatusCode);
        var enUtc = await LeerAsync<TicketsDeArticulo>(ctx.Admin, Url(ctx.IdEmpresa, cola));
        var enUtcSiguiente = await LeerAsync<TicketsDeArticulo>(
            ctx.Admin, Url(ctx.IdEmpresa, cola, Dia.AddDays(1), Dia.AddDays(1)));

        Assert.Equal(500m, Assert.Single(enArt.Tickets).Total);
        Assert.Empty(enArtSiguiente.Tickets);
        Assert.Empty(enUtc.Tickets);
        Assert.Equal(500m, Assert.Single(enUtcSiguiente.Tickets).Total);
    }

    [Fact]
    public async Task ElComprobanteYElClienteSeArmanComoEnElListadoDeVentas()
    {
        var ctx = await PrepararAsync(nameof(ElComprobanteYElClienteSeArmanComoEnElListadoDeVentas));
        var cola = await SembrarArticuloAsync(ctx, "Cola");
        var conCliente = await SembrarTicketAsync(ctx, [(cola, 1m, 100m)], numero: 42);
        var final = await SembrarTicketAsync(
            ctx, [(cola, 1m, 100m)], consumidorFinal: true, numero: 43, fecha: Mediodia.AddMinutes(-5));
        var nombreTipo = await NombreDeTipoAsync(ctx.IdTipoTx);

        var resultado = await LeerAsync<TicketsDeArticulo>(ctx.Admin, Url(ctx.IdEmpresa, cola));

        var a = resultado.Tickets.Single(t => t.IdComprobante == conCliente);
        var b = resultado.Tickets.Single(t => t.IdComprobante == final);
        Assert.Equal($"{nombreTipo} {NumeroDeComprobante.Formatear(ctx.IdPuntoVenta, 42)}", a.Comprobante);
        Assert.Equal($"{nombreTipo} {NumeroDeComprobante.Formatear(ctx.IdPuntoVenta, 43)}", b.Comprobante);
        Assert.Equal("Cliente de prueba", a.Cliente);
        Assert.Null(b.Cliente);
        Assert.Equal(Mediodia, a.Fecha);
    }

    private async Task<string> NombreDeTipoAsync(int idTipo)
    {
        await using var db = fixture.CrearContextoDeAplicacion(TenantActualFijo.Plataforma);
        return await db.TiposComprobante.Where(t => t.Id == idTipo).Select(t => t.Nombre).SingleAsync();
    }

    [Fact]
    public async Task UnArticuloAjenoInexistenteOSinVentasDevuelveListaVacia()
    {
        var ctxA = await PrepararAsync(nameof(UnArticuloAjenoInexistenteOSinVentasDevuelveListaVacia) + "-A");
        var ctxB = await PrepararAsync(nameof(UnArticuloAjenoInexistenteOSinVentasDevuelveListaVacia) + "-B");
        var propio = await SembrarArticuloAsync(ctxA, "Propio");
        var sinVentas = await SembrarArticuloAsync(ctxA, "Sin ventas");
        var ajeno = await SembrarArticuloAsync(ctxB, "Ajeno");
        await SembrarTicketAsync(ctxA, [(propio, 1m, 100m)]);
        await SembrarTicketAsync(ctxB, [(ajeno, 1m, 100m)]);

        foreach (var id in new[] { ajeno, sinVentas, 987_654_321 })
        {
            var resultado = await LeerAsync<TicketsDeArticulo>(ctxA.Admin, Url(ctxA.IdEmpresa, id));
            Assert.Empty(resultado.Tickets);
            Assert.Equal(0m, resultado.Total);
            Assert.Equal(0m, resultado.CantidadTotal);
        }
    }

    [Fact]
    public async Task UnArticuloDadoDeBajaConservaSusTicketsConLaDescripcionDeLaLinea()
    {
        var ctx = await PrepararAsync(nameof(UnArticuloDadoDeBajaConservaSusTicketsConLaDescripcionDeLaLinea));
        var cola = await SembrarArticuloAsync(ctx, "Cola");
        await SembrarTicketAsync(ctx, [(cola, 1m, 100m)]);
        await using (var db = fixture.CrearContextoDeAplicacion(new TenantActualFijo(ModoDeAcceso.Tenant, ctx.IdTenant)))
        {
            var articulo = await db.Articulos.SingleAsync(a => a.Id == cola);
            articulo.DeletedAt = DateTimeOffset.UtcNow;
            await db.SaveChangesAsync();
        }

        var resultado = await LeerAsync<TicketsDeArticulo>(ctx.Admin, Url(ctx.IdEmpresa, cola));

        Assert.Single(resultado.Tickets);
        Assert.Equal("snapshot", resultado.Descripcion);
    }

    [Fact]
    public async Task UnVendedorRecibe403YUnSupervisorPuedeLeer()
    {
        var ctx = await PrepararAsync(nameof(UnVendedorRecibe403YUnSupervisorPuedeLeer));
        var cola = await SembrarArticuloAsync(ctx, "Cola");
        await SembrarTicketAsync(ctx, [(cola, 1m, 100m)]);

        var vendedor = await ctx.Vendedor.GetAsync(Url(ctx.IdEmpresa, cola));
        var supervisor = await LeerAsync<TicketsDeArticulo>(ctx.Supervisor, Url(ctx.IdEmpresa, cola));

        Assert.Equal(HttpStatusCode.Forbidden, vendedor.StatusCode);
        Assert.Single(supervisor.Tickets);
    }

    [Fact]
    public async Task UnaEmpresaAjenaDevuelve404YUnPuntoDeVentaAjeno400()
    {
        var ctxA = await PrepararAsync(nameof(UnaEmpresaAjenaDevuelve404YUnPuntoDeVentaAjeno400) + "-A");
        var ctxB = await PrepararAsync(nameof(UnaEmpresaAjenaDevuelve404YUnPuntoDeVentaAjeno400) + "-B");
        var cola = await SembrarArticuloAsync(ctxA, "Cola");

        var empresaAjena = await ctxA.Admin.GetAsync(Url(ctxB.IdEmpresa, cola));
        var pvAjeno = await ctxA.Admin.GetAsync(Url(ctxA.IdEmpresa, cola, idPuntoVenta: ctxB.IdPuntoVenta));

        Assert.Equal(HttpStatusCode.NotFound, empresaAjena.StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, pvAjeno.StatusCode);
    }
}
