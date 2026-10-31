using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Ways.Application.Abstracciones;
using Ways.Application.Caja;
using Ways.Application.Compras;
using Ways.Application.CuentaCorriente;
using Ways.Application.Pagos;
using Ways.Application.Organizacion;
using Ways.Application.Usuarios;
using Ways.Domain.Articulos;
using Ways.Domain.Catalogos;
using Ways.Domain.Pagos;
using Ways.Domain.Organizacion;
using Ways.Domain.Proveedores;
using Ways.Infrastructure.Multitenancy;

namespace Ways.IntegrationTests;

/// <summary>
/// E3-S2: lo pagado de una compra sale de la suma de sus aplicaciones vivas, y lo aplicado/disponible de cada pago de
/// su suma de aplicaciones. Se leen por el detalle de compra, el listado de administración, el estado de cuenta del
/// proveedor y <c>GET /api/proveedores/{id}/saldo</c>.
/// </summary>
[Collection("Ways.IntegrationTests secuencial")]
public class AplicacionesDePagoLectoresTests(WaysApiFixture fixture) : IClassFixture<WaysApiFixture>
{
    private const string PasswordRoot = "root";
    private const string MailRoot = "test@test.com";

    private static readonly JsonSerializerOptions OpcionesJson = new()
    {
        PropertyNameCaseInsensitive = true,
        Converters = { new System.Text.Json.Serialization.JsonStringEnumConverter() }
    };

    private sealed record Contexto(
        int IdTenant, int IdEmpresa, int IdPuntoVenta, HttpClient Admin, int IdProveedor,
        int IdArticulo, int IdAlicuotaIva21, int IdTipoCFA, int IdMedioEfectivo);

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

        await using var db = fixture.CrearContextoDeAplicacion(TenantActualFijo.Plataforma);
        var ahora = DateTimeOffset.UtcNow;

        var area = new Area { IdTenant = resultado.IdTenant, Nombre = "Lectores-area", Orden = 1, CreatedAt = ahora, UpdatedAt = ahora };
        db.Areas.Add(area);
        await db.SaveChangesAsync();

        var idAlicuotaIva21 = await db.AlicuotasIva.Where(a => a.Nombre == "21%").Select(a => a.Id).FirstAsync();

        await using var dbTenant = fixture.CrearContextoDeAplicacion(new TenantActualFijo(ModoDeAcceso.Tenant, resultado.IdTenant));
        var idMedioEfectivo = await dbTenant.MediosPago
            .Where(m => m.Comportamiento == ComportamientoMedioPago.Efectivo).Select(m => m.Id).FirstAsync();

        var condicionFiscal = new CondicionFiscal { Codigo = $"{nombre}-CF", Nombre = nombre, CreatedAt = ahora, UpdatedAt = ahora };
        db.CondicionesFiscales.Add(condicionFiscal);
        await db.SaveChangesAsync();

        var proveedor = new Proveedor
        {
            IdTenant = resultado.IdTenant, RazonSocial = nombre, IdCondicionFiscal = condicionFiscal.Id,
            CreatedAt = ahora, UpdatedAt = ahora
        };
        db.Proveedores.Add(proveedor);
        await db.SaveChangesAsync();

        var articulo = new Articulo
        {
            IdTenant = resultado.IdTenant, CodigoInterno = $"{nombre}-{Guid.NewGuid():N}", Nombre = "Articulo",
            IdArea = area.Id, IdAlicuotaIva = idAlicuotaIva21, UnidadVenta = UnidadVenta.Unidad, EsProducto = true,
            CreatedAt = ahora, UpdatedAt = ahora
        };
        db.Articulos.Add(articulo);
        await db.SaveChangesAsync();

        var idTipoCFA = await db.TiposComprobante.Where(t => t.Codigo == "C-FA").Select(t => t.Id).SingleAsync();

        return new Contexto(
            resultado.IdTenant, resultado.IdEmpresa, resultado.IdPuntoVenta, admin, proveedor.Id,
            articulo.Id, idAlicuotaIva21, idTipoCFA, idMedioEfectivo);
    }

    /// <summary>Compra confirmada de 10 x 100 con IVA 21 %: total 1210.</summary>
    private static async Task<CompraDetalle> CrearYConfirmarCompraAsync(Contexto ctx, string numeroExterno = "0001-00000001")
    {
        var solicitud = new SolicitudDeCompra(
            ctx.IdProveedor, ctx.IdTipoCFA, ctx.IdPuntoVenta, numeroExterno, FechaDelNegocio.Hoy(), null,
            [new LineaDeCompraSolicitada(ctx.IdArticulo, "Item de prueba", 10m, null, null, 100m, 0m, ctx.IdAlicuotaIva21, true)]);
        var respuestaCrear = await ctx.Admin.PostAsJsonAsync("/api/compras", solicitud);
        var cuerpoCrear = await respuestaCrear.Content.ReadAsStringAsync();
        Assert.True(respuestaCrear.StatusCode == HttpStatusCode.Created, cuerpoCrear);
        var creada = JsonSerializer.Deserialize<CompraDetalle>(cuerpoCrear, OpcionesJson)!;

        var respuestaConfirmar = await ctx.Admin.PostAsync($"/api/compras/{creada.Id}/confirmar", null);
        var cuerpoConfirmar = await respuestaConfirmar.Content.ReadAsStringAsync();
        Assert.True(respuestaConfirmar.StatusCode == HttpStatusCode.OK, cuerpoConfirmar);
        return JsonSerializer.Deserialize<CompraDetalle>(cuerpoConfirmar, OpcionesJson)!;
    }

    private static async Task<PagoRegistrado> AltaAdminAsync(Contexto ctx, decimal importe, int? idComprobanteCompra)
    {
        var solicitud = PagoDeUnMedio.Administracion(
            FechaDelNegocio.Hoy(), ctx.IdEmpresa, ctx.IdPuntoVenta, CategoriaPago.Proveedor, ctx.IdProveedor, null,
            "Pago de prueba", null, ctx.IdMedioEfectivo, null, importe, idComprobanteCompra);
        var respuesta = await ctx.Admin.PostAsJsonAsync("/api/pagos/administracion", solicitud);
        var cuerpo = await respuesta.Content.ReadAsStringAsync();
        Assert.True(respuesta.StatusCode == HttpStatusCode.Created, cuerpo);
        return JsonSerializer.Deserialize<PagoRegistrado>(cuerpo, OpcionesJson)!;
    }

    private static async Task<CompraDetalle> DetalleAsync(Contexto ctx, int idCompra)
    {
        var respuesta = await ctx.Admin.GetAsync($"/api/compras/{idCompra}");
        var cuerpo = await respuesta.Content.ReadAsStringAsync();
        Assert.True(respuesta.StatusCode == HttpStatusCode.OK, cuerpo);
        return JsonSerializer.Deserialize<CompraDetalle>(cuerpo, OpcionesJson)!;
    }

    private static async Task<IReadOnlyList<PagoDeAdministracionListado>> ListadoDeAdministracionAsync(Contexto ctx)
    {
        var respuesta = await ctx.Admin.GetAsync($"/api/pagos/administracion?idProveedor={ctx.IdProveedor}&tamanio=200");
        var cuerpo = await respuesta.Content.ReadAsStringAsync();
        Assert.True(respuesta.StatusCode == HttpStatusCode.OK, cuerpo);
        return JsonSerializer.Deserialize<PaginaDePagosDeAdministracion>(cuerpo, OpcionesJson)!.Items;
    }

    private static async Task<PaginaDeEstadoDeCuentaDeProveedor> EstadoDeCuentaAsync(Contexto ctx)
    {
        var respuesta = await ctx.Admin.GetAsync($"/api/proveedores/{ctx.IdProveedor}/cuenta-corriente?historico=true");
        var cuerpo = await respuesta.Content.ReadAsStringAsync();
        Assert.True(respuesta.StatusCode == HttpStatusCode.OK, cuerpo);
        return JsonSerializer.Deserialize<PaginaDeEstadoDeCuentaDeProveedor>(cuerpo, OpcionesJson)!;
    }

    // ---- pagado por compra -----------------------------------------------------------------------------------

    [Fact]
    public async Task LoPagadoDeLaCompraEsLaSumaDeLasAplicacionesDeSusPagos()
    {
        var ctx = await PrepararAsync(nameof(LoPagadoDeLaCompraEsLaSumaDeLasAplicacionesDeSusPagos));
        var compra = await CrearYConfirmarCompraAsync(ctx);
        // Dos pagos vinculados que juntos superan el total: la suma cruda de los pagos (1400) no es lo pagado; las
        // aplicaciones suman exactamente el total (800 + 410), que es lo que distingue esta fórmula de la anterior.
        await AltaAdminAsync(ctx, 800m, compra.Id);
        await AltaAdminAsync(ctx, 600m, compra.Id);

        var detalle = await DetalleAsync(ctx, compra.Id);

        Assert.Equal(compra.Total, detalle.Pagado);
        Assert.NotEqual(1400m, detalle.Pagado);
        Assert.Equal(0m, detalle.SaldoPendiente);
    }

    [Fact]
    public async Task VincularUnPagoExistenteSumaSuAplicacionAlPagadoDeLaCompra()
    {
        var ctx = await PrepararAsync(nameof(VincularUnPagoExistenteSumaSuAplicacionAlPagadoDeLaCompra));
        var compra = await CrearYConfirmarCompraAsync(ctx);
        var sinCompra = await AltaAdminAsync(ctx, 555m, idComprobanteCompra: null);
        Assert.Equal(0m, (await DetalleAsync(ctx, compra.Id)).Pagado);

        var respuesta = await ctx.Admin.PostAsJsonAsync(
            $"/api/pagos/{sinCompra.Id}/vincular-compra", new SolicitudDeVincularCompra(compra.Id));
        Assert.True(respuesta.StatusCode == HttpStatusCode.OK, await respuesta.Content.ReadAsStringAsync());

        Assert.Equal(555m, (await DetalleAsync(ctx, compra.Id)).Pagado);
    }

    [Fact]
    public async Task DosPagosQueSuperanElTotalDejanLoPagadoEnElTotalYElExcedenteComoDisponible()
    {
        var ctx = await PrepararAsync(nameof(DosPagosQueSuperanElTotalDejanLoPagadoEnElTotalYElExcedenteComoDisponible));
        var compra = await CrearYConfirmarCompraAsync(ctx);
        var primero = await AltaAdminAsync(ctx, 1000m, compra.Id);
        var segundo = await AltaAdminAsync(ctx, 500m, compra.Id);

        var detalle = await DetalleAsync(ctx, compra.Id);
        Assert.Equal(compra.Total, detalle.Pagado);
        Assert.Equal(0m, detalle.SaldoPendiente);

        var listado = await ListadoDeAdministracionAsync(ctx);
        Assert.Equal(0m, listado.Single(p => p.Id == primero.Id).Disponible);
        var delSegundo = listado.Single(p => p.Id == segundo.Id);
        Assert.Equal(compra.Total - 1000m, delSegundo.Aplicado);
        Assert.Equal(500m - (compra.Total - 1000m), delSegundo.Disponible);
    }

    [Fact]
    public async Task UnPagoLegadoSobreImputadoNoHaceQueLoPagadoSupereElTotal()
    {
        var ctx = await PrepararAsync(nameof(UnPagoLegadoSobreImputadoNoHaceQueLoPagadoSupereElTotal));
        var compra = await CrearYConfirmarCompraAsync(ctx);
        var pago = await AltaAdminAsync(ctx, 800m, compra.Id);

        // Se edita hacia arriba por encima del total: el vínculo legado queda sobre-imputado y la aplicación se corta.
        var edicion = PagoDeUnMedio.Edicion(
            CategoriaPago.Proveedor, ctx.IdProveedor, null, "Concepto editado", null, ctx.IdMedioEfectivo, null, 5000m);
        var editar = await ctx.Admin.PutAsJsonAsync($"/api/pagos/administracion/{pago.Id}", edicion);
        Assert.True(editar.StatusCode == HttpStatusCode.OK, await editar.Content.ReadAsStringAsync());

        var detalle = await DetalleAsync(ctx, compra.Id);
        Assert.Equal(compra.Total, detalle.Pagado);
        Assert.Equal(0m, detalle.SaldoPendiente);

        var delPago = (await ListadoDeAdministracionAsync(ctx)).Single(p => p.Id == pago.Id);
        Assert.Equal(5000m - compra.Total, delPago.Disponible);
    }

    [Fact]
    public async Task LaBajaDeUnPagoLiberaSuAplicacionYLoPagadoNoCuentaLaLiberada()
    {
        var ctx = await PrepararAsync(nameof(LaBajaDeUnPagoLiberaSuAplicacionYLoPagadoNoCuentaLaLiberada));
        var compra = await CrearYConfirmarCompraAsync(ctx);
        var queSeDaDeBaja = await AltaAdminAsync(ctx, 300m, compra.Id);
        await AltaAdminAsync(ctx, 100m, compra.Id);
        Assert.Equal(400m, (await DetalleAsync(ctx, compra.Id)).Pagado);

        var baja = await ctx.Admin.DeleteAsync($"/api/pagos/administracion/{queSeDaDeBaja.Id}");
        Assert.Equal(HttpStatusCode.NoContent, baja.StatusCode);

        // Mutation-proof: si el lector contara también las aplicaciones liberadas, daría 400 y no 100.
        Assert.Equal(100m, (await DetalleAsync(ctx, compra.Id)).Pagado);
    }

    [Fact]
    public async Task LaAnulacionDeLaCompraLiberaSusAplicacionesYElPagoVuelveEnteroAlDisponible()
    {
        var ctx = await PrepararAsync(nameof(LaAnulacionDeLaCompraLiberaSusAplicacionesYElPagoVuelveEnteroAlDisponible));
        var compra = await CrearYConfirmarCompraAsync(ctx);
        var pago = await AltaAdminAsync(ctx, 300m, compra.Id);
        Assert.Equal(0m, (await ListadoDeAdministracionAsync(ctx)).Single(p => p.Id == pago.Id).Disponible);

        var anulada = await ctx.Admin.PostAsync($"/api/compras/{compra.Id}/anular", null);
        Assert.True(anulada.StatusCode == HttpStatusCode.OK, await anulada.Content.ReadAsStringAsync());

        Assert.Equal(0m, (await DetalleAsync(ctx, compra.Id)).Pagado);
        var delPago = (await ListadoDeAdministracionAsync(ctx)).Single(p => p.Id == pago.Id);
        Assert.Equal(0m, delPago.Aplicado);
        Assert.Equal(300m, delPago.Disponible);
        Assert.Empty(delPago.Aplicaciones!);
    }

    // ---- disponible por pago, listado de administración ------------------------------------------------------

    [Fact]
    public async Task ElListadoDeAdministracionExponeAplicadoDisponibleYLasAplicacionesDeCadaPago()
    {
        var ctx = await PrepararAsync(nameof(ElListadoDeAdministracionExponeAplicadoDisponibleYLasAplicacionesDeCadaPago));
        var compra = await CrearYConfirmarCompraAsync(ctx, "0001-00000077");
        var entero = await AltaAdminAsync(ctx, 500m, compra.Id);
        var parcial = await AltaAdminAsync(ctx, 1000m, compra.Id);
        var suelto = await AltaAdminAsync(ctx, 70m, idComprobanteCompra: null);

        var listado = await ListadoDeAdministracionAsync(ctx);

        var delEntero = listado.Single(p => p.Id == entero.Id);
        Assert.Equal(500m, delEntero.Aplicado);
        Assert.Equal(0m, delEntero.Disponible);
        var aplicacionEntera = Assert.Single(delEntero.Aplicaciones!);
        Assert.Equal(compra.Id, aplicacionEntera.IdComprobanteCompra);
        Assert.Equal("00001-00000077", aplicacionEntera.NumeroCompra);
        Assert.Equal(500m, aplicacionEntera.Importe);

        var delParcial = listado.Single(p => p.Id == parcial.Id);
        Assert.Equal(compra.Total - 500m, delParcial.Aplicado);
        Assert.Equal(1000m - (compra.Total - 500m), delParcial.Disponible);
        Assert.Equal(compra.Total - 500m, Assert.Single(delParcial.Aplicaciones!).Importe);

        var delSuelto = listado.Single(p => p.Id == suelto.Id);
        Assert.Equal(0m, delSuelto.Aplicado);
        Assert.Equal(70m, delSuelto.Disponible);
        Assert.Empty(delSuelto.Aplicaciones!);
    }

    // ---- estado de cuenta del proveedor ----------------------------------------------------------------------

    [Fact]
    public async Task ElEstadoDeCuentaExponeElDisponibleDeCadaPagoYElCreditoDelEncabezado()
    {
        var ctx = await PrepararAsync(nameof(ElEstadoDeCuentaExponeElDisponibleDeCadaPagoYElCreditoDelEncabezado));
        var compra = await CrearYConfirmarCompraAsync(ctx, "0001-00000011");
        // Una segunda compra sin pagar mantiene el saldo positivo: saldoAFavor y disponibleEnPagos no son lo mismo.
        await CrearYConfirmarCompraAsync(ctx, "0001-00000012");
        var parcial = await AltaAdminAsync(ctx, 1500m, compra.Id);
        var suelto = await AltaAdminAsync(ctx, 70m, idComprobanteCompra: null);

        var estado = await EstadoDeCuentaAsync(ctx);

        var disponibleParcial = 1500m - compra.Total;
        Assert.Equal(disponibleParcial + 70m, estado.Header.DisponibleEnPagos);
        Assert.Equal(0m, estado.Header.SaldoAFavor);
        Assert.Equal(compra.Total * 2 - 1500m - 70m, estado.Header.Saldo);

        var filaParcial = estado.Items.Single(m => m.Tipo == Ways.Domain.CuentaCorriente.TipoMovimientoCcProveedor.Pago && m.IdPago == parcial.Id);
        Assert.Equal(disponibleParcial, filaParcial.DisponibleDelPago);
        var filaSuelta = estado.Items.Single(m => m.Tipo == Ways.Domain.CuentaCorriente.TipoMovimientoCcProveedor.Pago && m.IdPago == suelto.Id);
        Assert.Equal(70m, filaSuelta.DisponibleDelPago);
        Assert.All(
            estado.Items.Where(m => m.Tipo != Ways.Domain.CuentaCorriente.TipoMovimientoCcProveedor.Pago),
            m => Assert.Null(m.DisponibleDelPago));
    }

    [Fact]
    public async Task ConSaldoNegativoElEncabezadoExponeElSaldoAFavor()
    {
        var ctx = await PrepararAsync(nameof(ConSaldoNegativoElEncabezadoExponeElSaldoAFavor));
        var compra = await CrearYConfirmarCompraAsync(ctx);
        await AltaAdminAsync(ctx, compra.Total + 90m, compra.Id);

        var estado = await EstadoDeCuentaAsync(ctx);

        Assert.Equal(-90m, estado.Header.Saldo);
        Assert.Equal(90m, estado.Header.SaldoAFavor);
        Assert.Equal(90m, estado.Header.DisponibleEnPagos);
    }

    // ---- el saldo no cambia de forma --------------------------------------------------------------------------

    [Fact]
    public async Task ElEndpointDeSaldoConservaSuFormaYTomaElPagadoDeLasAplicaciones()
    {
        var ctx = await PrepararAsync(nameof(ElEndpointDeSaldoConservaSuFormaYTomaElPagadoDeLasAplicaciones));
        var compra = await CrearYConfirmarCompraAsync(ctx);
        await AltaAdminAsync(ctx, 400m, compra.Id);

        var respuesta = await ctx.Admin.GetAsync($"/api/proveedores/{ctx.IdProveedor}/saldo");
        var cuerpo = await respuesta.Content.ReadAsStringAsync();
        Assert.True(respuesta.StatusCode == HttpStatusCode.OK, cuerpo);
        using var json = JsonDocument.Parse(cuerpo);

        Assert.Equal(
            ["compras", "idProveedor", "saldo"],
            json.RootElement.EnumerateObject().Select(p => p.Name).Order(StringComparer.Ordinal).ToArray());
        var fila = json.RootElement.GetProperty("compras").EnumerateArray().Single();
        Assert.Equal(
            ["estadoPago", "idComprobanteCompra", "numeroExterno", "pagado", "total"],
            fila.EnumerateObject().Select(p => p.Name).Order(StringComparer.Ordinal).ToArray());
        Assert.Equal(400m, fila.GetProperty("pagado").GetDecimal());
    }
}
