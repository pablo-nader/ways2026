using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Ways.Application.Abstracciones;
using Ways.Application.Caja;
using Ways.Application.Compras;
using Ways.Application.Pagos;
using Ways.Application.Organizacion;
using Ways.Application.Usuarios;
using Ways.Domain.Articulos;
using Ways.Domain.Catalogos;
using Ways.Domain.Pagos;
using Ways.Domain.Organizacion;
using Ways.Domain.Proveedores;
using Ways.Infrastructure.Multitenancy;
using Ways.Infrastructure.Persistencia;

namespace Ways.IntegrationTests;

/// <summary>
/// E3-S1b: doble escritura de <c>aplicaciones_pago</c> junto con cada vínculo <c>pagos.id_comprobante_compra</c>
/// (alta del POS, alta administrativa, pagar compra, vincular), con tope en el saldo de la compra según sus
/// aplicaciones vivas, y liberación en la anulación de la compra y en la baja del pago. La edición no deja el
/// importe por debajo de lo aplicado (<c>409 pago_importe_menor_que_aplicado</c>).
/// </summary>
[Collection("Ways.IntegrationTests secuencial")]
public class AplicacionesDePagoEscritoresTests(WaysApiFixture fixture) : IClassFixture<WaysApiFixture>
{
    private const string PasswordRoot = "root";
    private const string MailRoot = "test@test.com";

    private static readonly JsonSerializerOptions OpcionesJson = new()
    {
        PropertyNameCaseInsensitive = true,
        Converters = { new System.Text.Json.Serialization.JsonStringEnumConverter() }
    };

    private sealed record Contexto(
        int IdTenant, int IdEmpresa, int IdPuntoVenta, HttpClient Admin, int IdProveedor, int IdProveedor2,
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

        var area = new Area { IdTenant = resultado.IdTenant, Nombre = "Aplicaciones-area", Orden = 1, CreatedAt = ahora, UpdatedAt = ahora };
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
        var proveedor2 = new Proveedor
        {
            IdTenant = resultado.IdTenant, RazonSocial = $"{nombre}-otro", IdCondicionFiscal = condicionFiscal.Id,
            CreatedAt = ahora, UpdatedAt = ahora
        };
        db.Proveedores.AddRange(proveedor, proveedor2);
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
            resultado.IdTenant, resultado.IdEmpresa, resultado.IdPuntoVenta, admin, proveedor.Id, proveedor2.Id,
            articulo.Id, idAlicuotaIva21, idTipoCFA, idMedioEfectivo);
    }

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

    private static async Task<PagoRegistrado> AltaAdminAsync(
        Contexto ctx, decimal importe, int? idComprobanteCompra, CategoriaPago categoria = CategoriaPago.Proveedor,
        int? idProveedor = null, int? idEmpresa = null, int? idPuntoVenta = null)
    {
        var solicitud = new SolicitudDePagoDeAdministracion(
            FechaDelNegocio.Hoy(), idEmpresa ?? ctx.IdEmpresa, idPuntoVenta ?? ctx.IdPuntoVenta, categoria,
            idProveedor ?? (categoria == CategoriaPago.Proveedor ? ctx.IdProveedor : null), null,
            "Pago de prueba", null, ctx.IdMedioEfectivo, null, importe, idComprobanteCompra);
        var respuesta = await ctx.Admin.PostAsJsonAsync("/api/gastos/administracion", solicitud);
        var cuerpo = await respuesta.Content.ReadAsStringAsync();
        Assert.True(respuesta.StatusCode == HttpStatusCode.Created, cuerpo);
        return JsonSerializer.Deserialize<PagoRegistrado>(cuerpo, OpcionesJson)!;
    }

    private static async Task<string> CodigoDeErrorAsync(HttpResponseMessage respuesta) =>
        JsonDocument.Parse(await respuesta.Content.ReadAsStringAsync()).RootElement.GetProperty("codigo").GetString()!;

    private static SolicitudDeEdicionDePago Edicion(
        Contexto ctx, decimal importe, int? idProveedor = null) =>
        new(CategoriaPago.Proveedor, idProveedor ?? ctx.IdProveedor, null, "Concepto editado", null,
            ctx.IdMedioEfectivo, null, importe);

    private async Task<List<AplicacionPago>> AplicacionesDelPagoAsync(Contexto ctx, int idPago)
    {
        await using var db = fixture.CrearContextoDeAplicacion(new TenantActualFijo(ModoDeAcceso.Tenant, ctx.IdTenant));
        return await db.AplicacionesPago.IgnoreQueryFilters()
            .Where(a => a.IdPago == idPago).OrderBy(a => a.Id).ToListAsync();
    }

    private async Task<List<AplicacionPago>> AplicacionesDeLaCompraAsync(Contexto ctx, int idCompra)
    {
        await using var db = fixture.CrearContextoDeAplicacion(new TenantActualFijo(ModoDeAcceso.Tenant, ctx.IdTenant));
        return await db.AplicacionesPago.IgnoreQueryFilters()
            .Where(a => a.IdComprobanteCompra == idCompra).OrderBy(a => a.Id).ToListAsync();
    }

    // ---- altas ---------------------------------------------------------------------------------------------

    [Fact]
    public async Task ElAltaAdministrativaLigadaEscribeLaAplicacionYElSobranteSeCortaAlSaldo()
    {
        var ctx = await PrepararAsync(nameof(ElAltaAdministrativaLigadaEscribeLaAplicacionYElSobranteSeCortaAlSaldo));
        var compra = await CrearYConfirmarCompraAsync(ctx);

        var primero = await AltaAdminAsync(ctx, 300m, compra.Id);
        var segundo = await AltaAdminAsync(ctx, compra.Total, compra.Id);
        var tercero = await AltaAdminAsync(ctx, 50m, compra.Id);

        var aplicacionPrimero = Assert.Single(await AplicacionesDelPagoAsync(ctx, primero.Id));
        Assert.Equal(300m, aplicacionPrimero.Importe);
        Assert.Equal(OrigenAplicacionPago.PagoDeCompra, aplicacionPrimero.Origen);
        Assert.Equal(compra.Id, aplicacionPrimero.IdComprobanteCompra);

        var aplicacionSegundo = Assert.Single(await AplicacionesDelPagoAsync(ctx, segundo.Id));
        Assert.Equal(compra.Total - 300m, aplicacionSegundo.Importe);

        // El vínculo heredado se conserva aunque no quede saldo: el excedente queda sin aplicar.
        Assert.Equal(compra.Id, tercero.IdComprobanteCompra);
        Assert.Empty(await AplicacionesDelPagoAsync(ctx, tercero.Id));
    }

    [Fact]
    public async Task ElAltaDelPosLigadaEscribeLaAplicacion()
    {
        var ctx = await PrepararAsync(nameof(ElAltaDelPosLigadaEscribeLaAplicacion));
        var compra = await CrearYConfirmarCompraAsync(ctx);

        var turno = await ctx.Admin.PostAsJsonAsync(
            "/api/caja/turnos", new SolicitudDeApertura(ctx.IdPuntoVenta, 0m, "Apertura de soporte"));
        Assert.Equal(HttpStatusCode.Created, turno.StatusCode);

        var respuesta = await ctx.Admin.PostAsJsonAsync(
            "/api/gastos",
            new SolicitudDePago(
                ctx.IdPuntoVenta, CategoriaPago.Proveedor, ctx.IdProveedor, null, "Pago del POS", null,
                ctx.IdMedioEfectivo, null, 120m, compra.Id));
        var cuerpo = await respuesta.Content.ReadAsStringAsync();
        Assert.True(respuesta.StatusCode == HttpStatusCode.Created, cuerpo);
        var pago = JsonSerializer.Deserialize<PagoRegistrado>(cuerpo, OpcionesJson)!;

        var aplicacion = Assert.Single(await AplicacionesDelPagoAsync(ctx, pago.Id));
        Assert.Equal(120m, aplicacion.Importe);
        Assert.Equal(OrigenAplicacionPago.PagoDeCompra, aplicacion.Origen);
    }

    [Fact]
    public async Task PagarLaCompraEscribeLaAplicacionPorElImporteDelPago()
    {
        var ctx = await PrepararAsync(nameof(PagarLaCompraEscribeLaAplicacionPorElImporteDelPago));
        var compra = await CrearYConfirmarCompraAsync(ctx);

        var respuesta = await ctx.Admin.PostAsJsonAsync(
            $"/api/compras/{compra.Id}/pagos",
            new SolicitudDePagoDeCompra(FechaDelNegocio.Hoy(), 200m, ctx.IdMedioEfectivo, null));
        var cuerpo = await respuesta.Content.ReadAsStringAsync();
        Assert.True(respuesta.StatusCode == HttpStatusCode.Created, cuerpo);
        var resultado = JsonSerializer.Deserialize<ResultadoDePagoDeCompra>(cuerpo, OpcionesJson)!;

        var aplicacion = Assert.Single(await AplicacionesDelPagoAsync(ctx, resultado.Pago.Id));
        Assert.Equal(200m, aplicacion.Importe);
        Assert.Equal(OrigenAplicacionPago.PagoDeCompra, aplicacion.Origen);
    }

    [Fact]
    public async Task VincularUnPagoExistenteEscribeUnaAplicacionDeOrigenAplicacionCortadaAlSaldo()
    {
        var ctx = await PrepararAsync(nameof(VincularUnPagoExistenteEscribeUnaAplicacionDeOrigenAplicacionCortadaAlSaldo));
        var compra = await CrearYConfirmarCompraAsync(ctx);
        await AltaAdminAsync(ctx, compra.Total - 100m, compra.Id);
        var sinCompra = await AltaAdminAsync(ctx, 500m, idComprobanteCompra: null);

        var respuesta = await ctx.Admin.PostAsJsonAsync(
            $"/api/gastos/{sinCompra.Id}/vincular-compra", new SolicitudDeVincularCompra(compra.Id));
        Assert.True(respuesta.StatusCode == HttpStatusCode.OK, await respuesta.Content.ReadAsStringAsync());

        var aplicacion = Assert.Single(await AplicacionesDelPagoAsync(ctx, sinCompra.Id));
        Assert.Equal(100m, aplicacion.Importe);
        Assert.Equal(OrigenAplicacionPago.Aplicacion, aplicacion.Origen);
    }

    [Fact]
    public async Task UnPagoDeOtraEmpresaConservaElVinculoHeredadoSinAplicacion()
    {
        var ctx = await PrepararAsync(nameof(UnPagoDeOtraEmpresaConservaElVinculoHeredadoSinAplicacion));
        var compra = await CrearYConfirmarCompraAsync(ctx);

        int idOtraEmpresa;
        int idOtroPuntoVenta;
        await using (var db = fixture.CrearContextoDeAplicacion(new TenantActualFijo(ModoDeAcceso.Tenant, ctx.IdTenant)))
        {
            var ahora = DateTimeOffset.UtcNow;
            var empresa = new Empresa { IdTenant = ctx.IdTenant, RazonSocial = "Otra empresa", CreatedAt = ahora, UpdatedAt = ahora };
            db.Empresas.Add(empresa);
            await db.SaveChangesAsync();
            var puntoVenta = new PuntoVenta
            {
                IdTenant = ctx.IdTenant, IdEmpresa = empresa.Id, Nombre = "Otra empresa - Local", CreatedAt = ahora, UpdatedAt = ahora
            };
            db.PuntosVenta.Add(puntoVenta);
            await db.SaveChangesAsync();
            idOtraEmpresa = empresa.Id;
            idOtroPuntoVenta = puntoVenta.Id;
        }

        var pago = await AltaAdminAsync(ctx, 100m, compra.Id, idEmpresa: idOtraEmpresa, idPuntoVenta: idOtroPuntoVenta);

        Assert.Equal(compra.Id, pago.IdComprobanteCompra);
        Assert.Empty(await AplicacionesDelPagoAsync(ctx, pago.Id));
    }

    // ---- liberación ------------------------------------------------------------------------------------------

    [Fact]
    public async Task AnularLaCompraLiberaSusAplicacionesYElPagoQuedaComoCredito()
    {
        var ctx = await PrepararAsync(nameof(AnularLaCompraLiberaSusAplicacionesYElPagoQuedaComoCredito));
        var compra = await CrearYConfirmarCompraAsync(ctx);
        var pago = await AltaAdminAsync(ctx, 300m, compra.Id);
        Assert.Single(await AplicacionesDelPagoAsync(ctx, pago.Id));

        var respuesta = await ctx.Admin.PostAsync($"/api/compras/{compra.Id}/anular", null);
        Assert.True(respuesta.StatusCode == HttpStatusCode.OK, await respuesta.Content.ReadAsStringAsync());

        var aplicaciones = await AplicacionesDeLaCompraAsync(ctx, compra.Id);
        var aplicacion = Assert.Single(aplicaciones);
        Assert.NotNull(aplicacion.DeletedAt);

        await using var db = fixture.CrearContextoDeAplicacion(new TenantActualFijo(ModoDeAcceso.Tenant, ctx.IdTenant));
        Assert.Equal(300m, await db.Pagos.Where(p => p.Id == pago.Id).Select(p => p.Importe).SingleAsync());
    }

    [Fact]
    public async Task LaBajaDelPagoLiberaSusAplicaciones()
    {
        var ctx = await PrepararAsync(nameof(LaBajaDelPagoLiberaSusAplicaciones));
        var compra = await CrearYConfirmarCompraAsync(ctx);
        var pago = await AltaAdminAsync(ctx, 300m, compra.Id);

        var respuesta = await ctx.Admin.DeleteAsync($"/api/gastos/administracion/{pago.Id}");
        Assert.Equal(HttpStatusCode.NoContent, respuesta.StatusCode);

        var aplicacion = Assert.Single(await AplicacionesDelPagoAsync(ctx, pago.Id));
        Assert.NotNull(aplicacion.DeletedAt);
    }

    // ---- edición -----------------------------------------------------------------------------------------------

    [Fact]
    public async Task EditarElImporteDebajoDeLoAplicadoEsUn409YNoCambiaNada()
    {
        var ctx = await PrepararAsync(nameof(EditarElImporteDebajoDeLoAplicadoEsUn409YNoCambiaNada));
        var compra = await CrearYConfirmarCompraAsync(ctx);
        var pago = await AltaAdminAsync(ctx, 300m, compra.Id);

        var respuesta = await ctx.Admin.PutAsJsonAsync($"/api/gastos/administracion/{pago.Id}", Edicion(ctx, 250m));

        Assert.Equal(HttpStatusCode.Conflict, respuesta.StatusCode);
        Assert.Equal("pago_importe_menor_que_aplicado", await CodigoDeErrorAsync(respuesta));

        await using var db = fixture.CrearContextoDeAplicacion(new TenantActualFijo(ModoDeAcceso.Tenant, ctx.IdTenant));
        Assert.Equal(300m, await db.Pagos.Where(p => p.Id == pago.Id).Select(p => p.Importe).SingleAsync());
        Assert.Equal(300m, Assert.Single(await AplicacionesDelPagoAsync(ctx, pago.Id)).Importe);
    }

    [Fact]
    public async Task EditarElImporteSinBajarDeLoAplicadoEsValido()
    {
        var ctx = await PrepararAsync(nameof(EditarElImporteSinBajarDeLoAplicadoEsValido));
        var compra = await CrearYConfirmarCompraAsync(ctx);
        var pago = await AltaAdminAsync(ctx, 300m, compra.Id);

        var respuesta = await ctx.Admin.PutAsJsonAsync($"/api/gastos/administracion/{pago.Id}", Edicion(ctx, 350m));

        Assert.True(respuesta.StatusCode == HttpStatusCode.OK, await respuesta.Content.ReadAsStringAsync());
        Assert.Equal(300m, Assert.Single(await AplicacionesDelPagoAsync(ctx, pago.Id)).Importe);
    }

    [Fact]
    public async Task CambiarElProveedorDeUnPagoAplicadoSigueDandoProveedorNoCoincide()
    {
        var ctx = await PrepararAsync(nameof(CambiarElProveedorDeUnPagoAplicadoSigueDandoProveedorNoCoincide));
        var compra = await CrearYConfirmarCompraAsync(ctx);
        var pago = await AltaAdminAsync(ctx, 300m, compra.Id);

        var respuesta = await ctx.Admin.PutAsJsonAsync(
            $"/api/gastos/administracion/{pago.Id}", Edicion(ctx, 300m, idProveedor: ctx.IdProveedor2));

        Assert.Equal(HttpStatusCode.BadRequest, respuesta.StatusCode);
        Assert.Equal("proveedor_no_coincide_con_la_compra", await CodigoDeErrorAsync(respuesta));
        Assert.Single(await AplicacionesDelPagoAsync(ctx, pago.Id));
    }
}
