using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Ways.Application;
using Ways.Application.Abstracciones;
using Ways.Application.Usuarios;
using Ways.Application.Compras;
using Ways.Application.Organizacion;
using Ways.Application.Parametros;
using Ways.Application.Pagos;
using Ways.Domain.Articulos;
using Ways.Domain.Catalogos;
using Ways.Domain.CuentaCorriente;
using Ways.Domain.Organizacion;
using Ways.Domain.Pagos;
using Ways.Domain.Proveedores;
using Ways.Infrastructure.Multitenancy;
using Ways.Infrastructure.Persistencia;

namespace Ways.IntegrationTests;

/// <summary>
/// E4: redondeo del proveedor. Cerrar en cero al pagar una compra de más o de menos (1503,50 pagada con 1500; 1430,25 con
/// 1500), los dos ajustes explícitos, la tolerancia configurable y las compensaciones (anulación de la compra, baja, edición
/// y desaplicación). El invariante que todas comparten: el saldo del proveedor y lo pagado/disponible no se desfasan, y un
/// redondeo revertido pesa 0. Requieren Docker: la prueba es el job <c>backend-integration</c> de CI.
/// </summary>
[Collection("Ways.IntegrationTests secuencial")]
public class RedondeoDePagosTests(WaysApiFixture fixture) : IClassFixture<WaysApiFixture>
{
    private const string PasswordRoot = "root";
    private const string MailRoot = "test@test.com";

    private static readonly JsonSerializerOptions OpcionesJson = new()
    {
        PropertyNameCaseInsensitive = true,
        Converters = { new System.Text.Json.Serialization.JsonStringEnumConverter() }
    };

    private sealed record Contexto(
        int IdTenant, int IdEmpresa, int IdPuntoVenta, HttpClient Admin, int IdProveedor, int IdArea, int IdEfectivo);

    private async Task<Contexto> PrepararAsync(string nombre)
    {
        using var root = fixture.CreateClient();
        var loginRoot = await root.PostAsJsonAsync("/api/auth/login", new SolicitudDeLogin(MailRoot, PasswordRoot));
        Assert.Equal(HttpStatusCode.OK, loginRoot.StatusCode);

        var mailAdmin = $"{nombre.ToLowerInvariant()}@ways.test";
        var respuesta = await root.PostAsJsonAsync(
            "/api/plataforma/tenants",
            new SolicitudDeAprovisionamiento(nombre, $"{nombre} SA", "Local 1", mailAdmin, ModoPuntoVenta.Web));
        Assert.Equal(HttpStatusCode.Created, respuesta.StatusCode);
        var resultado = (await respuesta.Content.ReadFromJsonAsync<ResultadoAprovisionamiento>())!;

        var admin = fixture.CreateClient();
        var loginAdmin = await admin.PostAsJsonAsync(
            "/api/auth/login", new SolicitudDeLogin(mailAdmin, resultado.PasswordTemporal));
        Assert.Equal(HttpStatusCode.OK, loginAdmin.StatusCode);

        await using var db = fixture.CrearContextoDeAplicacion(new TenantActualFijo(ModoDeAcceso.Tenant, resultado.IdTenant));
        var idEfectivo = await db.MediosPago
            .Where(m => m.Comportamiento == ComportamientoMedioPago.Efectivo).Select(m => m.Id).FirstAsync();

        await using var plataforma = fixture.CrearContextoDeAplicacion(TenantActualFijo.Plataforma);
        var ahora = DateTimeOffset.UtcNow;
        var area = new Area { IdTenant = resultado.IdTenant, Nombre = $"{nombre}-area", Orden = 1, CreatedAt = ahora, UpdatedAt = ahora };
        var condicion = new CondicionFiscal { Codigo = $"{nombre}-CF", Nombre = nombre, CreatedAt = ahora, UpdatedAt = ahora };
        plataforma.Areas.Add(area);
        plataforma.CondicionesFiscales.Add(condicion);
        await plataforma.SaveChangesAsync();
        var proveedor = new Proveedor
        {
            IdTenant = resultado.IdTenant, RazonSocial = nombre, IdCondicionFiscal = condicion.Id,
            CreatedAt = ahora, UpdatedAt = ahora
        };
        plataforma.Proveedores.Add(proveedor);
        await plataforma.SaveChangesAsync();

        return new Contexto(
            resultado.IdTenant, resultado.IdEmpresa, resultado.IdPuntoVenta, admin, proveedor.Id, area.Id, idEfectivo);
    }

    private WaysDbContext Db(Contexto ctx) =>
        fixture.CrearContextoDeAplicacion(new TenantActualFijo(ModoDeAcceso.Tenant, ctx.IdTenant));

    private static async Task<T> LeerAsync<T>(HttpResponseMessage respuesta, HttpStatusCode esperado)
    {
        var cuerpo = await respuesta.Content.ReadAsStringAsync();
        Assert.True(respuesta.StatusCode == esperado, cuerpo);
        return JsonSerializer.Deserialize<T>(cuerpo, OpcionesJson)!;
    }

    private static async Task<string> CodigoAsync(HttpResponseMessage respuesta) =>
        JsonDocument.Parse(await respuesta.Content.ReadAsStringAsync()).RootElement.GetProperty("codigo").GetString()!;

    private static DateOnly Ayer() => DateOnly.FromDateTime(DateTime.UtcNow.AddDays(-1));

    private async Task<(int IdCompra, decimal Total)> CrearYConfirmarCompraAsync(Contexto ctx)
    {
        await using var plataforma = fixture.CrearContextoDeAplicacion(TenantActualFijo.Plataforma);
        var ahora = DateTimeOffset.UtcNow;
        var idAlicuota = await plataforma.AlicuotasIva.Where(a => a.Nombre == "21%").Select(a => a.Id).FirstAsync();
        var idTipo = await plataforma.TiposComprobante.Where(t => t.Codigo == "C-FA").Select(t => t.Id).SingleAsync();
        var articulo = new Articulo
        {
            IdTenant = ctx.IdTenant, CodigoInterno = $"redondeo-{Guid.NewGuid():N}", Nombre = "Articulo",
            IdArea = ctx.IdArea, IdAlicuotaIva = idAlicuota, UnidadVenta = UnidadVenta.Unidad, EsProducto = true,
            CreatedAt = ahora, UpdatedAt = ahora
        };
        plataforma.Articulos.Add(articulo);
        await plataforma.SaveChangesAsync();

        var alta = await ctx.Admin.PostAsJsonAsync("/api/compras", new SolicitudDeCompra(
            ctx.IdProveedor, idTipo, ctx.IdPuntoVenta, "0001-00000001", DateOnly.FromDateTime(DateTime.UtcNow), null,
            [new LineaDeCompraSolicitada(articulo.Id, "Item", 10m, null, null, 100m, 0m, idAlicuota, true)]));
        var idCompra = (await LeerAsync<CompraDetalle>(alta, HttpStatusCode.Created)).Id;
        Assert.Equal(HttpStatusCode.OK, (await ctx.Admin.PostAsync($"/api/compras/{idCompra}/confirmar", null)).StatusCode);

        await using var db = Db(ctx);
        return (idCompra, await db.ComprobantesCompra.Where(c => c.Id == idCompra).Select(c => c.Total).SingleAsync());
    }

    private static async Task<HttpResponseMessage> PagarAsync(
        Contexto ctx, int idCompra, decimal importe, bool cerrarEnCero) =>
        await ctx.Admin.PostAsJsonAsync(
            $"/api/pagos/de-compra/{idCompra}",
            new SolicitudDePagoDeCompraConLineas(
                Ayer(), [new LineaDePago(ctx.IdEfectivo, importe, null)], null, null, cerrarEnCero));

    private static async Task<decimal> SaldoDelProveedorAsync(Contexto ctx) =>
        (await ctx.Admin.GetFromJsonAsync<SaldoDeProveedor>($"/api/proveedores/{ctx.IdProveedor}/saldo", OpcionesJson))!.Saldo;

    private static async Task<decimal> SaldoPendienteAsync(Contexto ctx, int idCompra) =>
        (await ctx.Admin.GetFromJsonAsync<CompraDetalle>($"/api/compras/{idCompra}", OpcionesJson))!.SaldoPendiente;

    private static async Task<decimal?> DisponibleAsync(Contexto ctx, int idPago) =>
        (await ctx.Admin.GetFromJsonAsync<PagoDeAdministracionListado>($"/api/pagos/administracion/{idPago}", OpcionesJson))!.Disponible;

    private async Task<List<MovimientoCuentaCorrienteProveedor>> RedondeosAsync(Contexto ctx)
    {
        await using var db = Db(ctx);
        return await db.MovimientosCuentaCorrienteProveedor
            .Where(m => m.IdProveedor == ctx.IdProveedor && m.Tipo == TipoMovimientoCcProveedor.Redondeo)
            .OrderBy(m => m.Id)
            .ToListAsync();
    }

    private static async Task EstablecerToleranciaAsync(Contexto ctx, string valor)
    {
        var respuesta = await ctx.Admin.PostAsJsonAsync(
            $"/api/parametros?idEmpresa={ctx.IdEmpresa}", new ParametroAlta("tolerancia_redondeo_pago_proveedor", valor, null));
        Assert.True(respuesta.IsSuccessStatusCode, await respuesta.Content.ReadAsStringAsync());
    }

    // ---- cerrarEnCero al pagar ---------------------------------------------------------------------------------

    [Fact]
    public async Task PagarDeMenosCerrandoEnCeroPerdonaLaDiferenciaYDejaLaCompraPagadaYElSaldoEnCero()
    {
        var ctx = await PrepararAsync(nameof(PagarDeMenosCerrandoEnCeroPerdonaLaDiferenciaYDejaLaCompraPagadaYElSaldoEnCero));
        var (idCompra, total) = await CrearYConfirmarCompraAsync(ctx);

        var resultado = await LeerAsync<ResultadoDePagoDeCompraConLineas>(
            await PagarAsync(ctx, idCompra, total - 3.50m, cerrarEnCero: true), HttpStatusCode.Created);

        Assert.Equal(3.50m, resultado.Redondeo);
        Assert.Equal(total, resultado.Pagado);
        Assert.Equal(0m, resultado.SaldoPendiente);
        Assert.Equal(0m, resultado.DisponibleDelPago);
        Assert.Equal(0m, await SaldoPendienteAsync(ctx, idCompra));
        Assert.Equal(0m, await SaldoDelProveedorAsync(ctx));

        var redondeo = Assert.Single(await RedondeosAsync(ctx));
        Assert.Equal((-3.50m, (int?)idCompra, (int?)null), (redondeo.Importe, redondeo.IdComprobanteCompra, redondeo.IdPago));
    }

    [Fact]
    public async Task PagarDeMasCerrandoEnCeroAbsorbeElExcedenteEnElPagoYNoQuedaCreditoNiSaldo()
    {
        var ctx = await PrepararAsync(nameof(PagarDeMasCerrandoEnCeroAbsorbeElExcedenteEnElPagoYNoQuedaCreditoNiSaldo));
        var (idCompra, total) = await CrearYConfirmarCompraAsync(ctx);

        var resultado = await LeerAsync<ResultadoDePagoDeCompraConLineas>(
            await PagarAsync(ctx, idCompra, total + 69.75m, cerrarEnCero: true), HttpStatusCode.Created);

        Assert.Equal(-69.75m, resultado.Redondeo);
        Assert.Equal(total, resultado.Pagado);
        Assert.Equal(0m, resultado.SaldoPendiente);
        Assert.Equal(0m, resultado.DisponibleDelPago);
        Assert.Equal(0m, await DisponibleAsync(ctx, resultado.Pago!.Id));
        Assert.Equal(0m, await SaldoDelProveedorAsync(ctx));

        var redondeo = Assert.Single(await RedondeosAsync(ctx));
        Assert.Equal((69.75m, (int?)null, (int?)resultado.Pago.Id), (redondeo.Importe, redondeo.IdComprobanteCompra, redondeo.IdPago));
    }

    [Fact]
    public async Task PagarSinCerrarEnCeroConservaElComportamientoDeSiempreYNoEscribeRedondeo()
    {
        var ctx = await PrepararAsync(nameof(PagarSinCerrarEnCeroConservaElComportamientoDeSiempreYNoEscribeRedondeo));
        var (idCompra, total) = await CrearYConfirmarCompraAsync(ctx);

        var resultado = await LeerAsync<ResultadoDePagoDeCompraConLineas>(
            await PagarAsync(ctx, idCompra, total - 3.50m, cerrarEnCero: false), HttpStatusCode.Created);

        Assert.Null(resultado.Redondeo);
        Assert.Equal(3.50m, resultado.SaldoPendiente);
        Assert.Empty(await RedondeosAsync(ctx));
    }

    [Theory]
    [InlineData(-600)]
    [InlineData(600)]
    public async Task UnaDiferenciaMayorQueLaToleranciaEsConflictoYNoEscribeNada(int diferencia)
    {
        var ctx = await PrepararAsync(nameof(UnaDiferenciaMayorQueLaToleranciaEsConflictoYNoEscribeNada) + diferencia);
        var (idCompra, total) = await CrearYConfirmarCompraAsync(ctx);
        var saldoAntes = await SaldoDelProveedorAsync(ctx);

        var respuesta = await PagarAsync(ctx, idCompra, total + diferencia, cerrarEnCero: true);

        Assert.Equal(HttpStatusCode.Conflict, respuesta.StatusCode);
        Assert.Equal("redondeo_excede_tolerancia", await CodigoAsync(respuesta));
        Assert.Equal(saldoAntes, await SaldoDelProveedorAsync(ctx));
        Assert.Equal(total, await SaldoPendienteAsync(ctx, idCompra));
        await using var db = Db(ctx);
        Assert.Equal(0, await db.Pagos.CountAsync(p => p.IdProveedor == ctx.IdProveedor));
        Assert.Empty(await RedondeosAsync(ctx));
    }

    [Fact]
    public async Task LaToleranciaConfigurableSeRespetaAlCerrarEnCero()
    {
        var ctx = await PrepararAsync(nameof(LaToleranciaConfigurableSeRespetaAlCerrarEnCero));
        var (idCompra, total) = await CrearYConfirmarCompraAsync(ctx);
        await EstablecerToleranciaAsync(ctx, "10");

        var excedida = await PagarAsync(ctx, idCompra, total + 69.75m, cerrarEnCero: true);
        Assert.Equal(HttpStatusCode.Conflict, excedida.StatusCode);
        Assert.Equal("redondeo_excede_tolerancia", await CodigoAsync(excedida));

        var dentro = await LeerAsync<ResultadoDePagoDeCompraConLineas>(
            await PagarAsync(ctx, idCompra, total - 3.50m, cerrarEnCero: true), HttpStatusCode.Created);
        Assert.Equal(3.50m, dentro.Redondeo);
        Assert.Equal(0m, dentro.SaldoPendiente);
    }

    [Theory]
    [InlineData("-1")]
    [InlineData("1.005")]
    [InlineData("\"abc\"")]
    public async Task UnaToleranciaInvalidaSeRechazaAlConfigurarla(string valor)
    {
        var ctx = await PrepararAsync(nameof(UnaToleranciaInvalidaSeRechazaAlConfigurarla) + valor.GetHashCode().ToString("x"));

        var respuesta = await ctx.Admin.PostAsJsonAsync(
            $"/api/parametros?idEmpresa={ctx.IdEmpresa}", new ParametroAlta("tolerancia_redondeo_pago_proveedor", valor, null));

        Assert.Equal(HttpStatusCode.BadRequest, respuesta.StatusCode);
    }

    [Fact]
    public async Task LosPagosAplicablesInformanLaToleranciaVigente()
    {
        var ctx = await PrepararAsync(nameof(LosPagosAplicablesInformanLaToleranciaVigente));
        var (idCompra, _) = await CrearYConfirmarCompraAsync(ctx);

        var porDefecto = await ctx.Admin.GetFromJsonAsync<PagosAplicables>($"/api/compras/{idCompra}/pagos-aplicables", OpcionesJson);
        Assert.Equal(500m, porDefecto!.ToleranciaRedondeo);

        await EstablecerToleranciaAsync(ctx, "25.5");
        var configurada = await ctx.Admin.GetFromJsonAsync<PagosAplicables>($"/api/compras/{idCompra}/pagos-aplicables", OpcionesJson);
        Assert.Equal(25.5m, configurada!.ToleranciaRedondeo);
    }

    // ---- ajustes explícitos ------------------------------------------------------------------------------------

    [Fact]
    public async Task AjustarElRedondeoDeUnaCompraConSaldoChicoLaDejaPagadaYLaSegundaVezNoTieneSaldo()
    {
        var ctx = await PrepararAsync(nameof(AjustarElRedondeoDeUnaCompraConSaldoChicoLaDejaPagadaYLaSegundaVezNoTieneSaldo));
        var (idCompra, total) = await CrearYConfirmarCompraAsync(ctx);
        await LeerAsync<ResultadoDePagoDeCompraConLineas>(await PagarAsync(ctx, idCompra, total - 3.50m, false), HttpStatusCode.Created);

        var resultado = await LeerAsync<ResultadoDeRedondeoDeCompra>(
            await ctx.Admin.PostAsync($"/api/compras/{idCompra}/ajuste-de-redondeo", null), HttpStatusCode.OK);

        Assert.Equal((idCompra, 3.50m, total, 0m), (resultado.IdCompra, resultado.Redondeo, resultado.Pagado, resultado.SaldoPendiente));
        Assert.Equal(0m, await SaldoPendienteAsync(ctx, idCompra));
        Assert.Equal(0m, await SaldoDelProveedorAsync(ctx));

        var otra = await ctx.Admin.PostAsync($"/api/compras/{idCompra}/ajuste-de-redondeo", null);
        Assert.Equal(HttpStatusCode.Conflict, otra.StatusCode);
        Assert.Equal("compra_sin_saldo_pendiente", await CodigoAsync(otra));
    }

    [Fact]
    public async Task AjustarElRedondeoDeUnaCompraConMasSaldoQueLaToleranciaOInexistenteRespondeLosCodigos()
    {
        var ctx = await PrepararAsync(nameof(AjustarElRedondeoDeUnaCompraConMasSaldoQueLaToleranciaOInexistenteRespondeLosCodigos));
        var (idCompra, _) = await CrearYConfirmarCompraAsync(ctx);
        await EstablecerToleranciaAsync(ctx, "10");

        var excedida = await ctx.Admin.PostAsync($"/api/compras/{idCompra}/ajuste-de-redondeo", null);
        Assert.Equal(HttpStatusCode.Conflict, excedida.StatusCode);
        Assert.Equal("redondeo_excede_tolerancia", await CodigoAsync(excedida));

        var inexistente = await ctx.Admin.PostAsync("/api/compras/999999999/ajuste-de-redondeo", null);
        Assert.Equal(HttpStatusCode.NotFound, inexistente.StatusCode);
        Assert.Empty(await RedondeosAsync(ctx));
    }

    [Fact]
    public async Task AjustarElRedondeoDeUnPagoConDisponibleChicoLoDejaEnCeroYLaSegundaVezNoTieneDisponible()
    {
        var ctx = await PrepararAsync(nameof(AjustarElRedondeoDeUnPagoConDisponibleChicoLoDejaEnCeroYLaSegundaVezNoTieneDisponible));
        var (idCompra, total) = await CrearYConfirmarCompraAsync(ctx);
        var pago = (await LeerAsync<ResultadoDePagoDeCompraConLineas>(
            await PagarAsync(ctx, idCompra, total + 69.75m, false), HttpStatusCode.Created)).Pago!;
        Assert.Equal(69.75m, await DisponibleAsync(ctx, pago.Id));

        var resultado = await LeerAsync<ResultadoDeRedondeoDePago>(
            await ctx.Admin.PostAsync($"/api/pagos/{pago.Id}/ajuste-de-redondeo", null), HttpStatusCode.OK);

        Assert.Equal((pago.Id, 69.75m, 0m), (resultado.IdPago, resultado.Redondeo, resultado.Disponible));
        Assert.Equal(0m, await DisponibleAsync(ctx, pago.Id));
        Assert.Equal(0m, await SaldoDelProveedorAsync(ctx));

        var otra = await ctx.Admin.PostAsync($"/api/pagos/{pago.Id}/ajuste-de-redondeo", null);
        Assert.Equal(HttpStatusCode.Conflict, otra.StatusCode);
        Assert.Equal("pago_sin_disponible", await CodigoAsync(otra));
    }

    [Fact]
    public async Task AjustarElRedondeoDeUnPagoSinProveedorODeMasQueLaToleranciaRespondeConflicto()
    {
        var ctx = await PrepararAsync(nameof(AjustarElRedondeoDeUnPagoSinProveedorODeMasQueLaToleranciaRespondeConflicto));
        var (idCompra, total) = await CrearYConfirmarCompraAsync(ctx);
        var conCredito = (await LeerAsync<ResultadoDePagoDeCompraConLineas>(
            await PagarAsync(ctx, idCompra, total + 69.75m, false), HttpStatusCode.Created)).Pago!;
        await EstablecerToleranciaAsync(ctx, "10");

        var excedida = await ctx.Admin.PostAsync($"/api/pagos/{conCredito.Id}/ajuste-de-redondeo", null);
        Assert.Equal("redondeo_excede_tolerancia", await CodigoAsync(excedida));

        var sinProveedor = await LeerAsync<PagoConLineas>(
            await ctx.Admin.PostAsJsonAsync(
                "/api/pagos/administracion",
                new SolicitudDePagoDeAdministracionConLineas(
                    Ayer(), ctx.IdEmpresa, ctx.IdPuntoVenta, CategoriaPago.Otros, null, null, "Sin proveedor", null, null,
                    [new LineaDePago(ctx.IdEfectivo, 5m, null)])),
            HttpStatusCode.Created);
        var respuesta = await ctx.Admin.PostAsync($"/api/pagos/{sinProveedor.Id}/ajuste-de-redondeo", null);
        Assert.Equal(HttpStatusCode.Conflict, respuesta.StatusCode);
        Assert.Equal("pago_sin_proveedor", await CodigoAsync(respuesta));
        Assert.Empty(await RedondeosAsync(ctx));
    }

    // ---- compensaciones ----------------------------------------------------------------------------------------

    [Fact]
    public async Task AnularUnaCompraConRedondeoLoRevierteYElSaldoDelProveedorQuedaSoloConElCreditoDelPago()
    {
        var ctx = await PrepararAsync(nameof(AnularUnaCompraConRedondeoLoRevierteYElSaldoDelProveedorQuedaSoloConElCreditoDelPago));
        var (idCompra, total) = await CrearYConfirmarCompraAsync(ctx);
        var pago = (await LeerAsync<ResultadoDePagoDeCompraConLineas>(
            await PagarAsync(ctx, idCompra, total - 3.50m, cerrarEnCero: true), HttpStatusCode.Created)).Pago!;

        Assert.Equal(HttpStatusCode.OK, (await ctx.Admin.PostAsync($"/api/compras/{idCompra}/anular", null)).StatusCode);

        Assert.Equal(0m, (await RedondeosAsync(ctx)).Sum(m => m.Importe));
        Assert.Equal(2, (await RedondeosAsync(ctx)).Count);
        Assert.Equal(-(total - 3.50m), await SaldoDelProveedorAsync(ctx));
        Assert.Equal(total - 3.50m, await DisponibleAsync(ctx, pago.Id));
    }

    [Fact]
    public async Task LaBajaDeUnPagoConRedondeoDelLadoDelPagoLoRevierteYLaCompraVuelveADeberElTotal()
    {
        var ctx = await PrepararAsync(nameof(LaBajaDeUnPagoConRedondeoDelLadoDelPagoLoRevierteYLaCompraVuelveADeberElTotal));
        var (idCompra, total) = await CrearYConfirmarCompraAsync(ctx);
        var pago = (await LeerAsync<ResultadoDePagoDeCompraConLineas>(
            await PagarAsync(ctx, idCompra, total + 69.75m, cerrarEnCero: true), HttpStatusCode.Created)).Pago!;

        var baja = await ctx.Admin.DeleteAsync($"/api/pagos/administracion/{pago.Id}");
        Assert.Equal(HttpStatusCode.NoContent, baja.StatusCode);

        Assert.Equal(0m, (await RedondeosAsync(ctx)).Sum(m => m.Importe));
        Assert.Equal(total, await SaldoPendienteAsync(ctx, idCompra));
        Assert.Equal(total, await SaldoDelProveedorAsync(ctx));
    }

    [Fact]
    public async Task LaBajaDeUnPagoConRedondeoDelLadoDeLaCompraLoRevierteYLaCompraVuelveADeberElTotal()
    {
        var ctx = await PrepararAsync(nameof(LaBajaDeUnPagoConRedondeoDelLadoDeLaCompraLoRevierteYLaCompraVuelveADeberElTotal));
        var (idCompra, total) = await CrearYConfirmarCompraAsync(ctx);
        var pago = (await LeerAsync<ResultadoDePagoDeCompraConLineas>(
            await PagarAsync(ctx, idCompra, total - 3.50m, cerrarEnCero: true), HttpStatusCode.Created)).Pago!;

        Assert.Equal(HttpStatusCode.NoContent, (await ctx.Admin.DeleteAsync($"/api/pagos/administracion/{pago.Id}")).StatusCode);

        Assert.Equal(0m, (await RedondeosAsync(ctx)).Sum(m => m.Importe));
        Assert.Equal(total, await SaldoPendienteAsync(ctx, idCompra));
        Assert.Equal(total, await SaldoDelProveedorAsync(ctx));
    }

    [Fact]
    public async Task DesaplicarElPagoDeUnaCompraConRedondeoRevierteElRedondeoDeLaCompra()
    {
        var ctx = await PrepararAsync(nameof(DesaplicarElPagoDeUnaCompraConRedondeoRevierteElRedondeoDeLaCompra));
        var (idCompra, total) = await CrearYConfirmarCompraAsync(ctx);
        var resultado = await LeerAsync<ResultadoDePagoDeCompraConLineas>(
            await PagarAsync(ctx, idCompra, total - 3.50m, cerrarEnCero: true), HttpStatusCode.Created);
        var aplicacion = Assert.Single(resultado.Aplicaciones);

        var desaplicar = await ctx.Admin.DeleteAsync($"/api/pagos/{resultado.Pago!.Id}/aplicaciones/{aplicacion.IdAplicacion}");
        Assert.Equal(HttpStatusCode.NoContent, desaplicar.StatusCode);

        Assert.Equal(0m, (await RedondeosAsync(ctx)).Sum(m => m.Importe));
        Assert.Equal(total, await SaldoPendienteAsync(ctx, idCompra));
        Assert.Equal(3.50m, await SaldoDelProveedorAsync(ctx));
    }

    [Fact]
    public async Task EditarElImporteDeUnPagoConRedondeoDelLadoDelPagoLoRevierte()
    {
        var ctx = await PrepararAsync(nameof(EditarElImporteDeUnPagoConRedondeoDelLadoDelPagoLoRevierte));
        var (idCompra, total) = await CrearYConfirmarCompraAsync(ctx);
        var pago = (await LeerAsync<ResultadoDePagoDeCompraConLineas>(
            await PagarAsync(ctx, idCompra, total + 69.75m, cerrarEnCero: true), HttpStatusCode.Created)).Pago!;

        var edicion = await ctx.Admin.PutAsJsonAsync(
            $"/api/pagos/administracion/{pago.Id}",
            new SolicitudDeEdicionDePagoConLineas(
                CategoriaPago.Proveedor, ctx.IdProveedor, null, "Concepto editado", null, null,
                [new LineaDePago(ctx.IdEfectivo, total, null)]));
        Assert.True(edicion.IsSuccessStatusCode, await edicion.Content.ReadAsStringAsync());

        Assert.Equal(0m, (await RedondeosAsync(ctx)).Sum(m => m.Importe));
        Assert.Equal(0m, await DisponibleAsync(ctx, pago.Id));
        Assert.Equal(0m, await SaldoPendienteAsync(ctx, idCompra));
        Assert.Equal(0m, await SaldoDelProveedorAsync(ctx));
    }

    [Fact]
    public async Task SacarDeLaCuentaDelProveedorUnPagoConRedondeoLoRevierte()
    {
        var ctx = await PrepararAsync(nameof(SacarDeLaCuentaDelProveedorUnPagoConRedondeoLoRevierte));
        var suelto = await LeerAsync<PagoConLineas>(
            await ctx.Admin.PostAsJsonAsync(
                "/api/pagos/administracion",
                new SolicitudDePagoDeAdministracionConLineas(
                    Ayer(), ctx.IdEmpresa, ctx.IdPuntoVenta, CategoriaPago.Proveedor, ctx.IdProveedor, null,
                    "A cuenta", null, null, [new LineaDePago(ctx.IdEfectivo, 50m, null)])),
            HttpStatusCode.Created);
        await LeerAsync<ResultadoDeRedondeoDePago>(
            await ctx.Admin.PostAsync($"/api/pagos/{suelto.Id}/ajuste-de-redondeo", null), HttpStatusCode.OK);
        Assert.Equal(0m, await SaldoDelProveedorAsync(ctx));

        var edicion = await ctx.Admin.PutAsJsonAsync(
            $"/api/pagos/administracion/{suelto.Id}",
            new SolicitudDeEdicionDePagoConLineas(
                CategoriaPago.Otros, null, null, "Ya no es del proveedor", null, null,
                [new LineaDePago(ctx.IdEfectivo, 50m, null)]));
        Assert.True(edicion.IsSuccessStatusCode, await edicion.Content.ReadAsStringAsync());

        Assert.Equal(0m, (await RedondeosAsync(ctx)).Sum(m => m.Importe));
        Assert.Equal(0m, await SaldoDelProveedorAsync(ctx));
    }

    [Fact]
    public async Task EditarElConceptoDeUnPagoConRedondeoNoLoRevierte()
    {
        var ctx = await PrepararAsync(nameof(EditarElConceptoDeUnPagoConRedondeoNoLoRevierte));
        var (idCompra, total) = await CrearYConfirmarCompraAsync(ctx);
        var pago = (await LeerAsync<ResultadoDePagoDeCompraConLineas>(
            await PagarAsync(ctx, idCompra, total + 69.75m, cerrarEnCero: true), HttpStatusCode.Created)).Pago!;

        var edicion = await ctx.Admin.PutAsJsonAsync(
            $"/api/pagos/administracion/{pago.Id}",
            new SolicitudDeEdicionDePagoConLineas(
                CategoriaPago.Proveedor, ctx.IdProveedor, null, "Solo el concepto", null, null,
                [new LineaDePago(ctx.IdEfectivo, total + 69.75m, null)]));
        Assert.True(edicion.IsSuccessStatusCode, await edicion.Content.ReadAsStringAsync());

        Assert.Equal(69.75m, (await RedondeosAsync(ctx)).Sum(m => m.Importe));
        Assert.Equal(0m, await DisponibleAsync(ctx, pago.Id));
    }
}
